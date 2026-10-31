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
        Assert.Equal(2, initialized.SchemaVersion);
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
        Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync()));
        Assert.Equal(2, second.SchemaVersion);
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

            Assert.Equal(2, upgraded.SchemaVersion);
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
            Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
