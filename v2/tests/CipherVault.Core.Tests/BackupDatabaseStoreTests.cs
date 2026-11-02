using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Backup;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CipherVault.Core.Tests;

public sealed class BackupDatabaseStoreTests
{
    private const string OwnershipMarkerName =
        ".cipher-vault-backup-owner";

    [Fact]
    public async Task CreatesEncryptedSnapshotWithVerifiedContent()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        var signals = new List<BackupOperationSignal>();
        var stepCodes = new List<int>();

        await fixture.CreateSnapshotAsync(
            staging,
            (signal, code) =>
            {
                signals.Add(signal);
                if (signal == BackupOperationSignal.StepCompleted)
                {
                    stepCodes.Add(code);
                }

                return ValueTask.CompletedTask;
            });

        Assert.Equal(
            1,
            signals.Count(signal =>
                signal == BackupOperationSignal.Initialized));
        Assert.Equal(
            1,
            signals.Count(signal =>
                signal == BackupOperationSignal.BackupFinishInvoked));
        Assert.Equal(
            1,
            signals.Count(signal =>
                signal == BackupOperationSignal.BackupFinished));
        Assert.Contains(stepCodes, code => code == raw.SQLITE_OK);
        Assert.Equal(raw.SQLITE_DONE, stepCodes[^1]);
        AssertNoStagingArtifacts(staging, keepDatabase: true);

        var databasePath = Path.Combine(staging, "vault.db");
        var bytes = await File.ReadAllBytesAsync(databasePath);
        Assert.False(
            bytes.AsSpan(0, Math.Min(bytes.Length, 16))
                .SequenceEqual("SQLite format 3\0"u8));

        await using var destination =
            await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(
                    staging,
                    fixture.Material.DatabaseKey,
                    CreateIfMissing: false));
        Assert.Equal("5000", await ScalarAsync(
            destination,
            "SELECT count(*) FROM backup_payload;"));
        Assert.Equal("2000", await ScalarAsync(
            destination,
            "SELECT length(value) FROM backup_payload WHERE id = 5000;"));
    }

    [Fact]
    public async Task CancellationRunsFinishExactlyOnceAndCleansFiles()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        using var cancellation = new CancellationTokenSource();
        var initializedCount = 0;
        var finishInvokedCount = 0;
        var finishCompletedCount = 0;
        var finishCodes = new List<int>();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await fixture.CreateSnapshotAsync(
                staging,
                (signal, code) =>
                {
                    switch (signal)
                    {
                        case BackupOperationSignal.Initialized:
                            initializedCount++;
                            break;
                        case BackupOperationSignal.StepCompleted
                            when (code == raw.SQLITE_OK):
                            cancellation.Cancel();
                            break;
                        case BackupOperationSignal.BackupFinishInvoked:
                            finishInvokedCount++;
                            throw new InvalidOperationException(
                                "intentional observer failure");
                        case BackupOperationSignal.BackupFinished:
                            finishCompletedCount++;
                            finishCodes.Add(code);
                            break;
                    }

                    return ValueTask.CompletedTask;
                },
                cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, initializedCount);
        Assert.Equal(1, finishInvokedCount);
        Assert.Equal(1, finishCompletedCount);
        Assert.Equal([raw.SQLITE_OK], finishCodes);
        AssertNoStagingArtifacts(staging, keepDatabase: false);
    }

    [Fact]
    public async Task RecoversFromRealBusyAfterCompetingExclusiveLock()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        var state = new BusyState();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await fixture.CreateSnapshotAsync(
            staging,
            async (signal, code) =>
            {
                if (signal == BackupOperationSignal.Initialized)
                {
                    state.Mark("initialized", stopwatch);
                    state.External = await OpenTargetConnectionAsync(
                        staging,
                        fixture.Material.DatabaseKey);
                    state.Mark("external-open", stopwatch);
                    state.Probe = await OpenTargetConnectionAsync(
                        staging,
                        fixture.Material.DatabaseKey);
                    state.Mark("probe-open", stopwatch);
                    await ExecuteAsync(
                        state.External,
                        "PRAGMA busy_timeout = 250; BEGIN EXCLUSIVE;");
                    state.Mark("exclusive", stopwatch);

                    await ExecuteAsync(
                        state.Probe,
                        "PRAGMA busy_timeout = 50;");
                    try
                    {
                        await ExecuteAsync(
                            state.Probe,
                            "BEGIN EXCLUSIVE;");
                    }
                    catch (SqliteException exception)
                    {
                        state.ProbeObservedBusy =
                            exception.SqliteErrorCode == raw.SQLITE_BUSY;
                    }
                    state.Mark("probe-check", stopwatch);
                }

                if (signal == BackupOperationSignal.StepCompleted
                    && !state.ExternalReleased)
                {
                    state.Mark("first-step", stopwatch);
                    state.FirstStepCode = code;
                    state.ExternalReleased = true;
                    if (state.External is not null)
                    {
                        await ExecuteAsync(state.External, "ROLLBACK;");
                    }

                    await DisposeAsync(state.Probe);
                    await DisposeAsync(state.External);
                    state.Probe = null;
                    state.External = null;
                    state.Mark("released", stopwatch);
                }
            });

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Busy recovery took {stopwatch.Elapsed}. {state.Diagnostics}");
        Assert.True(state.ProbeObservedBusy);
        Assert.Equal(raw.SQLITE_BUSY, state.FirstStepCode);
        AssertNoStagingArtifacts(staging, keepDatabase: true);

        await using var destination =
            await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(
                    staging,
                    fixture.Material.DatabaseKey,
                    CreateIfMissing: false));
        Assert.Equal("5000", await ScalarAsync(
            destination,
            "SELECT count(*) FROM backup_payload;"));
    }

    [Fact]
    public async Task SourceWriteReturnsRealBusyAndRecoversAfterRelease()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        var state = new SourceWriteLockState();

        await fixture.CreateSnapshotAsync(
            staging,
            (signal, code) =>
            {
                if (signal == BackupOperationSignal.Initialized)
                {
                    state.Acquire(fixture.Runtime);
                }

                if (signal == BackupOperationSignal.StepCompleted)
                {
                    if (state.FirstStepCode == -1)
                    {
                        state.FirstStepCode = code;
                    }

                    state.Release();
                }

                return ValueTask.CompletedTask;
            });

        Assert.True(state.ReaderWasActive);
        Assert.Equal(raw.SQLITE_BUSY, state.FirstStepCode);
        AssertNoStagingArtifacts(staging, keepDatabase: true);

        await using var destination =
            await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(
                    staging,
                    fixture.Material.DatabaseKey,
                    CreateIfMissing: false));
        Assert.Equal("5000", await ScalarAsync(
            destination,
            "SELECT count(*) FROM backup_payload;"));
    }

    [Fact]
    public async Task PersistentSourceWriteBusyFailsAfterFiveSecondsAndCleansFiles()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        var state = new SourceWriteLockState();
        var startedAt = TimeProvider.System.GetTimestamp();

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(
                async () => await fixture.CreateSnapshotAsync(
                    staging,
                    (signal, code) =>
                    {
                        if (signal == BackupOperationSignal.Initialized)
                        {
                            state.Acquire(fixture.Runtime);
                        }

                        if (signal == BackupOperationSignal.StepCompleted)
                        {
                            state.RecordCode(code);
                        }

                        return ValueTask.CompletedTask;
                    }));
            Assert.Equal(VaultErrorCode.DatabaseError, exception.Code);
            Assert.Contains("busy or locked", exception.Message);
            Assert.Contains(raw.SQLITE_BUSY, state.StepCodes);
            Assert.DoesNotContain(raw.SQLITE_LOCKED, state.StepCodes);
        }
        finally
        {
            state.Release();
        }

        var elapsed = TimeProvider.System.GetElapsedTime(startedAt);
        Assert.True(
            elapsed >= TimeSpan.FromSeconds(4.5),
            $"Persistent LOCKED failed too early: {elapsed}.");
        AssertNoStagingArtifacts(staging, keepDatabase: false);
    }

    [Fact]
    public async Task RejectsMissingAndNonCanonicalWorkingDirectories()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var missing = Path.Combine(
            fixture.Root,
            $"missing-{Guid.NewGuid():N}");

        var missingException = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.CreateSnapshotAsync(missing));
        Assert.Equal(VaultErrorCode.StorageError, missingException.Code);
        Assert.False(Directory.Exists(missing));

        var staging = fixture.CreateStagingDirectory();
        var nonCanonical = Path.Combine(staging, "nested", "..");
        var nonCanonicalException = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.CreateSnapshotAsync(nonCanonical));
        Assert.Equal(
            VaultErrorCode.StorageError,
            nonCanonicalException.Code);
        AssertNoStagingArtifacts(staging, keepDatabase: false);
    }

    [Fact]
    public async Task RejectsExistingTargetsAndPreservesUnrelatedFiles()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        var databasePath = Path.Combine(staging, "vault.db");
        var unrelatedPath = Path.Combine(staging, "keep.txt");
        await File.WriteAllTextAsync(databasePath, "sentinel");
        await File.WriteAllTextAsync(unrelatedPath, "keep");

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.CreateSnapshotAsync(staging));

        Assert.Equal(VaultErrorCode.StorageError, exception.Code);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(databasePath));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelatedPath));
        Assert.False(File.Exists(
            Path.Combine(staging, OwnershipMarkerName)));
    }

    [Fact]
    public async Task ExistingOwnershipMarkerIsNotRemoved()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        var markerPath = Path.Combine(staging, OwnershipMarkerName);
        await File.WriteAllTextAsync(markerPath, "other-owner");

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.CreateSnapshotAsync(staging));

        Assert.Equal(VaultErrorCode.StorageError, exception.Code);
        Assert.True(File.Exists(markerPath));
        Assert.Equal("other-owner", await File.ReadAllTextAsync(markerPath));
        Assert.False(File.Exists(Path.Combine(staging, "vault.db")));
    }

    [Fact]
    public async Task CancelledTokenExitsBeforeCreatingStagingFiles()
    {
        await using var fixture = await SourceFixture.CreateAsync();
        var staging = fixture.CreateStagingDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await fixture.CreateSnapshotAsync(
                staging,
                observer: null,
                cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    private static async Task<SqliteConnection> OpenTargetConnectionAsync(
        string staging,
        ReadOnlyMemory<byte> databaseKey)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(staging, "vault.db"),
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        try
        {
            await connection.OpenAsync();
            connection.DefaultTimeout = 1;
            await ExecuteAsync(connection, "PRAGMA cipher = 'chacha20';");
            await ExecuteAsync(
                connection,
                $"PRAGMA key = 'raw:{Convert.ToHexString(databaseKey.Span)}';");
            _ = await ScalarAsync(
                connection,
                "SELECT count(*) FROM sqlite_master;");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DisposeAsync(SqliteConnection? connection)
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task<string?> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static void AssertNoStagingArtifacts(
        string staging,
        bool keepDatabase)
    {
        Assert.False(File.Exists(
            Path.Combine(staging, OwnershipMarkerName)));
        Assert.False(File.Exists(Path.Combine(staging, "vault.db-wal")));
        Assert.False(File.Exists(Path.Combine(staging, "vault.db-shm")));
        Assert.False(File.Exists(Path.Combine(staging, "vault.db-journal")));
        Assert.Equal(
            keepDatabase,
            File.Exists(Path.Combine(staging, "vault.db")));
    }

    private sealed class BusyState
    {
        internal SqliteConnection? External { get; set; }

        internal SqliteConnection? Probe { get; set; }

        internal bool ProbeObservedBusy { get; set; }

        internal bool ExternalReleased { get; set; }

        internal int FirstStepCode { get; set; } = -1;

        internal string Diagnostics { get; private set; } = string.Empty;

        internal void Mark(string name, System.Diagnostics.Stopwatch stopwatch)
        {
            Diagnostics += $"{name}={stopwatch.Elapsed};";
        }
    }

    private sealed class SourceWriteLockState
    {
        private SqliteTransaction? transaction;
        private SqliteCommand? command;
        private SqliteDataReader? reader;

        internal int FirstStepCode { get; set; } = -1;

        internal bool ReaderWasActive { get; private set; }

        internal List<int> StepCodes { get; } = [];

        internal void RecordCode(int code)
        {
            if (StepCodes.Count == 0 || StepCodes[^1] != code)
            {
                StepCodes.Add(code);
            }
        }

        internal void Acquire(VaultDatabaseRuntime runtime)
        {
            var context = runtime.CreateOperationContext();
            try
            {
                transaction = context.Connection.BeginTransaction();
                command = context.Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    WITH RECURSIVE sequence(value) AS (
                        SELECT 1
                        UNION ALL
                        SELECT value + 1
                        FROM sequence
                        WHERE value < 1000
                    )
                    INSERT INTO backup_blocker(value)
                    SELECT printf('%02000d', value)
                    FROM sequence
                    RETURNING id;
                    """;
                reader = command.ExecuteReader();
                ReaderWasActive = reader.Read();
            }
            finally
            {
                context.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        internal void Release()
        {
            reader?.Dispose();
            reader = null;
            command?.Dispose();
            command = null;
            transaction?.Rollback();
            transaction?.Dispose();
            transaction = null;
        }
    }

    private sealed class SourceFixture : IAsyncDisposable
    {
        private SourceFixture(
            string root,
            VaultKeyMaterial material,
            VaultDatabaseRuntime runtime)
        {
            Root = root;
            Material = material;
            Runtime = runtime;
        }

        internal string Root { get; }

        internal VaultKeyMaterial Material { get; }

        internal VaultDatabaseRuntime Runtime { get; }

        internal string CreateStagingDirectory()
        {
            var staging = Path.Combine(
                Root,
                $"staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            return staging;
        }

        internal async Task CreateSnapshotAsync(
            string staging,
            BackupOperationObserver? observer = null,
            CancellationToken cancellationToken = default)
        {
            _ = await Runtime.ExecuteAsync(
                async (context, token) =>
                {
                    await BackupDatabaseStore.CreateEncryptedSnapshotAsync(
                        context,
                        staging,
                        Material.DatabaseKey,
                        observer,
                        token);
                    return 0;
                },
                cancellationToken);
        }

        internal static async Task<SourceFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-backup-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var material = new VaultKeyMaterial(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32));
            try
            {
                await SqliteMcVaultStore.InitializeAsync(root, material);
                var runtime = await VaultDatabaseRuntime.OpenAsync(
                    root,
                    material,
                    Guid.NewGuid(),
                    generation: 1);
                try
                {
                    _ = await runtime.ExecuteAsync(
                        async (context, token) =>
                        {
                            await using var command =
                                context.Connection.CreateCommand();
                            command.CommandText =
                                """
                                CREATE TABLE backup_payload(
                                    id INTEGER PRIMARY KEY,
                                    value TEXT NOT NULL
                                );
                                CREATE TABLE backup_blocker(
                                    id INTEGER PRIMARY KEY,
                                    value TEXT NOT NULL
                                );
                                WITH RECURSIVE sequence(value) AS (
                                    SELECT 1
                                    UNION ALL
                                    SELECT value + 1
                                    FROM sequence
                                    WHERE value < 5000
                                )
                                INSERT INTO backup_payload(id, value)
                                SELECT value, printf('%02000d', value)
                                FROM sequence;
                                """;
                            await command.ExecuteNonQueryAsync(token);
                            return 0;
                        });
                    return new SourceFixture(root, material, runtime);
                }
                catch
                {
                    await runtime.DisposeAsync();
                    throw;
                }
            }
            catch
            {
                material.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            Material.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
