using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Tests;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public async Task InitializeCreatesSchemaAndVerifiesFts5()
    {
        using var fixture = await Fixture.CreateAsync();

        var initialized = await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);
        var verified = await SqliteMcVaultStore.VerifyAsync(fixture.Root, fixture.Material);

        Assert.Equal(fixture.Material.VaultId, initialized.VaultId);
        Assert.Equal(5, initialized.SchemaVersion);
        Assert.Equal(initialized, verified);
    }

    [Fact]
    public async Task InitializationIsIdempotent()
    {
        using var fixture = await Fixture.CreateAsync();

        await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);
        var second = await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);

        await using var connection = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false);
        using var command = connection.Connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM schema_migrations;";
        Assert.Equal(5, Convert.ToInt32(await command.ExecuteScalarAsync()));
        Assert.Equal(5, second.SchemaVersion);
    }

    [Fact]
    public async Task HigherSchemaVersionIsRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);

        await using (var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false))
        {
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                """
                UPDATE vault_meta SET value = '99' WHERE key = 'schema_version';
                INSERT INTO schema_migrations(version, name, checksum_sha256, applied_at)
                VALUES (99, 'future', lower(hex(randomblob(32))), '2000-01-01T00:00:00Z');
                """,
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            SqliteMcVaultStore.VerifyAsync(fixture.Root, fixture.Material));
        Assert.Equal(VaultErrorCode.UnsupportedVersion, exception.Code);
    }

    [Fact]
    public async Task SchemaVersionMustMatchMigrationHistory()
    {
        using var fixture = await Fixture.CreateAsync();
        await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);

        await using (var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false))
        {
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                "UPDATE vault_meta SET value = '0' WHERE key = 'schema_version';",
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            SqliteMcVaultStore.VerifyAsync(fixture.Root, fixture.Material));
        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
    }

    [Fact]
    public async Task FailedMigrationRollsBackAllSchemaChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using var session = await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                root,
                material,
                create: true);
            var badMigration = new DatabaseMigration(
                1,
                "bad",
                "CREATE TABLE vault_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO missing_table VALUES (1);",
                "0".PadLeft(64, '0'));

            await Assert.ThrowsAnyAsync<SqliteException>(() =>
                DatabaseInitializer.EnsureSchemaAsync(
                    session.Connection,
                    material.VaultId,
                    DateTimeOffset.UtcNow,
                    [badMigration],
                    CancellationToken.None));

            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM sqlite_master
                WHERE name IN ('vault_meta', 'schema_migrations');
                """;
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PendingMigrationIsAppliedAndVersionAdvances()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        var migrations = DatabaseMigrationCatalog.Load();
        var initial = migrations.Take(1).ToArray();

        try
        {
            await using var session = await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                root,
                material,
                create: true);
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                initial,
                CancellationToken.None);

            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);

            Assert.Equal(5, upgraded.SchemaVersion);
            using var command = session.Connection.CreateCommand();
            command.CommandText =
                """
                SELECT count(*)
                FROM sqlite_master
                WHERE name IN (
                    'documents',
                    'document_revisions',
                    'tags',
                    'document_tags',
                    'document_links',
                    'document_fts',
                    'audit_events'
                );
                """;
            Assert.Equal(7, Convert.ToInt32(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT count(*) FROM schema_migrations;";
            Assert.Equal(5, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AuditMigrationsPreserveRowsIndexesAndAppendOnlyProtection()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-audit-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var historicalId = Guid.NewGuid().ToString("D");

        try
        {
            await using var session =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    root,
                    material,
                    create: true);
            var migrations = DatabaseMigrationCatalog.Load();
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations.Take(2).ToArray(),
                CancellationToken.None);
            using (var insertHistorical = session.Connection.CreateCommand())
            {
                insertHistorical.CommandText = """
                    INSERT INTO audit_events(
                        event_id, occurred_at, actor_kind, action,
                        document_id, revision, correlation_id, result, details_json)
                    VALUES(
                        $eventId, '2026-11-01T00:00:00Z', 'system', 'link_rebuild',
                        NULL, NULL, NULL, 'success', '{"kept":true}');
                    """;
                insertHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                await insertHistorical.ExecuteNonQueryAsync();
            }

            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);

            Assert.Equal(5, upgraded.SchemaVersion);
            using (var selectHistorical = session.Connection.CreateCommand())
            {
                selectHistorical.CommandText = """
                    SELECT action, details_json
                    FROM audit_events
                    WHERE event_id = $eventId;
                    """;
                selectHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                await using var reader =
                    await selectHistorical.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("link_rebuild", reader.GetString(0));
                Assert.Equal("{\"kept\":true}", reader.GetString(1));
            }

            using (var insertAudits = session.Connection.CreateCommand())
            {
                insertAudits.CommandText = """
                    INSERT INTO audit_events(
                        event_id, occurred_at, actor_kind, action,
                        document_id, revision, correlation_id, result, details_json)
                    VALUES
                        ($createId, '2026-11-01T00:00:01Z', 'user_ui',
                         'backup.create', NULL, NULL, NULL, 'success', '{}'),
                        ($downloadId, '2026-11-01T00:00:02Z', 'user_ui',
                         'backup.download', NULL, NULL, NULL, 'success', '{}'),
                        ($exportId, '2026-11-01T00:00:03Z', 'user_ui',
                         'export.plaintext', NULL, NULL, NULL, 'success', '{}'),
                        ($startedId, '2026-11-01T00:00:04Z', 'user_ui',
                         'export.plaintext', NULL, NULL, NULL, 'started', '{}');
                    """;
                insertAudits.Parameters.AddWithValue(
                    "$createId",
                    Guid.NewGuid().ToString("D"));
                insertAudits.Parameters.AddWithValue(
                    "$downloadId",
                    Guid.NewGuid().ToString("D"));
                insertAudits.Parameters.AddWithValue(
                    "$exportId",
                    Guid.NewGuid().ToString("D"));
                insertAudits.Parameters.AddWithValue(
                    "$startedId",
                    Guid.NewGuid().ToString("D"));
                await insertAudits.ExecuteNonQueryAsync();
            }

            using (var unknown = session.Connection.CreateCommand())
            {
                unknown.CommandText = """
                    INSERT INTO audit_events(
                        event_id, occurred_at, actor_kind, action,
                        document_id, revision, correlation_id, result, details_json)
                    VALUES(
                        $eventId, '2026-11-01T00:00:03Z', 'system', 'unknown_action',
                        NULL, NULL, NULL, 'success', '{}');
                    """;
                unknown.Parameters.AddWithValue(
                    "$eventId",
                    Guid.NewGuid().ToString("D"));
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => unknown.ExecuteNonQueryAsync());
                Assert.Contains(
                    "CHECK constraint failed",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            using (var updateHistorical = session.Connection.CreateCommand())
            {
                updateHistorical.CommandText =
                    "UPDATE audit_events SET result = 'failed' WHERE event_id = $eventId;";
                updateHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => updateHistorical.ExecuteNonQueryAsync());
                Assert.Contains(
                    "append-only",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            using (var deleteHistorical = session.Connection.CreateCommand())
            {
                deleteHistorical.CommandText =
                    "DELETE FROM audit_events WHERE event_id = $eventId;";
                deleteHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => deleteHistorical.ExecuteNonQueryAsync());
                Assert.Contains(
                    "append-only",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            using (var objects = session.Connection.CreateCommand())
            {
                objects.CommandText = """
                    SELECT count(*) FROM sqlite_master
                    WHERE name IN (
                        'ix_audit_occurred',
                        'ix_audit_document',
                        'ix_audit_action',
                        'ix_audit_correlation',
                        'trg_audit_no_update',
                        'trg_audit_no_delete'
                    );
                    """;
                Assert.Equal(
                    6,
                    Convert.ToInt32(await objects.ExecuteScalarAsync()));
            }

            using (var foreignKeys = session.Connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                await using var reader =
                    await foreignKeys.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync());
            }

            Assert.Equal(
                "ok",
                await SqliteMcDatabaseFactory.ExecuteScalarStringAsync(
                    session.Connection,
                    "PRAGMA integrity_check;",
                    CancellationToken.None));
        }
        finally
        {
            material.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, VaultKeyMaterial material)
        {
            Root = root;
            Material = material;
        }

        public string Root { get; }

        public VaultKeyMaterial Material { get; }

        public static Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-schema-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            VaultFilePermissions.ProtectDirectory(root);
            return Task.FromResult(new Fixture(
                root,
                new VaultKeyMaterial(
                    Guid.NewGuid(),
                    RandomNumberGenerator.GetBytes(32),
                    RandomNumberGenerator.GetBytes(32))));
        }

        public void Dispose()
        {
            Material.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
