using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using SQLitePCL;

namespace CipherVault.Application.Tests;

public sealed class BackupSnapshotLockTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task LockDuringSteppedSnapshotReleasesGateAndCleansStaging()
    {
        var timing = new VaultSessionTimingOptions(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.CreateAsync(timing);
        await fixture.Manager.UnlockAsync(Password);
        var session = fixture.Manager.Session!;
        var activeSessionCount =
            SqliteMcDatabaseSession.ActiveSessionCountForTest;
        await CreateLargePayloadAsync(fixture.Manager);
        var staging = Path.Combine(
            fixture.Parent,
            $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var stepStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObserver = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOkObserved = 0;

        var operation = fixture.Manager.ExecuteAsync(
            async (context, cancellationToken) =>
            {
                await BackupDatabaseStore.CreateEncryptedSnapshotAsync(
                    context,
                    staging,
                    session.KeyMaterial.DatabaseKey,
                    async (signal, code) =>
                    {
                        if (signal == BackupOperationSignal.StepCompleted
                            && code == raw.SQLITE_OK
                            && Interlocked.Exchange(
                                ref firstOkObserved,
                                1) == 0)
                        {
                            stepStarted.TrySetResult();
                            await releaseObserver.Task.WaitAsync(
                                TimeSpan.FromSeconds(10));
                        }
                    },
                    cancellationToken);
                return 0;
            }).AsTask();

        await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var lockTask = fixture.Manager.LockAsync();
        await WaitForCancellationAsync(session);
        releaseObserver.TrySetResult();

        var locked = await lockTask.WaitAsync(TimeSpan.FromSeconds(5));
        var operationException = await Assert.ThrowsAsync<VaultException>(
            async () => await operation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(VaultSessionState.Locked, locked.State);
        Assert.Equal(VaultErrorCode.VaultLocked, operationException.Code);
        Assert.Null(fixture.Manager.Session);
        Assert.True(session.Database.IsDisposedForTest);
        Assert.Equal(
            System.Data.ConnectionState.Closed,
            session.Database.ConnectionStateForTest);
        Assert.Equal(
            activeSessionCount - 1,
            SqliteMcDatabaseSession.ActiveSessionCountForTest);
        Assert.False(File.Exists(Path.Combine(staging, "vault.db")));
        Assert.False(File.Exists(
            Path.Combine(staging, ".cipher-vault-backup-owner")));
        Assert.False(File.Exists(Path.Combine(staging, "vault.db-wal")));
        Assert.False(File.Exists(Path.Combine(staging, "vault.db-shm")));
        Assert.False(File.Exists(Path.Combine(staging, "vault.db-journal")));
    }

    private static async Task CreateLargePayloadAsync(
        VaultSessionManager manager)
    {
        _ = await manager.ExecuteAsync(
            async (context, cancellationToken) =>
            {
                await using var command = context.Connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE lock_backup_payload(
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
                    INSERT INTO lock_backup_payload(id, value)
                    SELECT value, printf('%02000d', value)
                    FROM sequence;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            });
    }

    private static async Task WaitForCancellationAsync(VaultSession session)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (session.OperationCancellation.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            "Vault Session cancellation was not observed.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string parent, VaultSessionManager manager)
        {
            Parent = parent;
            Manager = manager;
        }

        internal string Parent { get; }

        internal VaultSessionManager Manager { get; }

        internal static async Task<Fixture> CreateAsync(
            VaultSessionTimingOptions timing)
        {
            var parent = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-backup-lock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(parent);
            var manager = await VaultSessionManager.CreateForTestAsync(
                Path.Combine(parent, "vault"),
                options: null,
                timing,
                timeProvider: null);
            try
            {
                await manager.InitializeAsync(Password);
                return new Fixture(parent, manager);
            }
            catch
            {
                await manager.DisposeAsync();
                await TestDirectoryCleanup.DeleteBestEffortAsync(parent);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            await TestDirectoryCleanup.DeleteBestEffortAsync(Parent);
        }
    }
}
