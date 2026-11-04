using CipherVault.Core.Backup;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;

namespace CipherVault.Core.Tests;

public sealed class BackupSchemaCompatibilityTests
{
    private const string TestPassword = "correct horse battery staple";

    [Fact]
    public void CompatibilityPolicyUsesFixedBaseline()
    {
        Assert.Equal(5, BackupCompatibilityPolicy.MinimumSupportedSchemaVersion);
        Assert.False(BackupCompatibilityPolicy.IsSupported(4));
        Assert.True(BackupCompatibilityPolicy.IsSupported(5));
        Assert.True(BackupCompatibilityPolicy.IsSupported(6));
        Assert.True(BackupCompatibilityPolicy.IsSupported(7));
        Assert.True(BackupCompatibilityPolicy.IsSupported(8));
        Assert.False(BackupCompatibilityPolicy.IsSupported(9));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    public async Task RestoreRejectsSchemaOutsideSupportedRange(
        int schemaVersion)
    {
        await using var fixture = await BackupFixture.CreateAsync(
            schemaVersion);
        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => VerifyAsync(
                fixture.PackagePath,
                BackupSchemaPolicy.AllowSupportedOlderVersions));

        Assert.Equal("restore_schema_unsupported", exception.Code);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task RestoreAcceptsSupportedOlderSchemaWithCompatibilityPolicy(
        int schemaVersion)
    {
        await using var fixture = await BackupFixture.CreateAsync(schemaVersion);

        var result = await VerifyAsync(
            fixture.PackagePath,
            BackupSchemaPolicy.AllowSupportedOlderVersions);

        Assert.Equal(schemaVersion, result.SchemaVersion);
        Assert.Equal(BackupConstants.FormatVersion, result.Manifest.FormatVersion);
    }

    [Fact]
    public async Task CurrentOnlyRejectsOlderSchema()
    {
        await using var fixture = await BackupFixture.CreateAsync(4);

        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => VerifyAsync(
                fixture.PackagePath,
                BackupSchemaPolicy.CurrentOnly));

        Assert.Equal("restore_schema_unsupported", exception.Code);
    }

    [Fact]
    public async Task InvalidMigrationHistoryIsRejectedAsDatabaseInvalid()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            5,
            corruptMigrationHistory: true);

        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => VerifyAsync(
                fixture.PackagePath,
                BackupSchemaPolicy.AllowSupportedOlderVersions));

        Assert.Equal("restore_database_invalid", exception.Code);
    }

    [Fact]
    public async Task CorruptDatabaseIsRejectedAsDatabaseInvalid()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            5,
            corruptDatabase: true);

        var exception = await Assert.ThrowsAsync<BackupPackageValidationException>(
            () => VerifyAsync(
                fixture.PackagePath,
                BackupSchemaPolicy.AllowSupportedOlderVersions));

        Assert.Equal("restore_database_invalid", exception.Code);
    }

    private static async Task<BackupVerificationResult> VerifyAsync(
        string packagePath,
        BackupSchemaPolicy policy)
    {
        var staging = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-schema-verify-{Guid.NewGuid():N}");
        try
        {
            using var credential = new InteractivePasswordCredential(
                TestPassword);
            return await BackupPackageVerifier.VerifyAsync(
                new BackupVerificationRequest(
                    packagePath,
                    staging,
                    credential,
                    policy),
                CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private sealed class BackupFixture : IAsyncDisposable
    {
        private readonly string root;

        private BackupFixture(
            string root,
            string packagePath)
        {
            this.root = root;
            PackagePath = packagePath;
        }

        public string PackagePath { get; }

        public static async Task<BackupFixture> CreateAsync(
            int schemaVersion,
            bool corruptMigrationHistory = false,
            bool corruptDatabase = false)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-schema-fixture-{Guid.NewGuid():N}");
            var vaultDirectory = Path.Combine(root, "vault");
            var packagePath = Path.Combine(root, "backup.cvbackup");
            Directory.CreateDirectory(vaultDirectory);

            var creation = VaultKeyService.Create(
                TestPassword,
                Guid.NewGuid());
            using var material = creation.KeyMaterial;
            var keyPath = Path.Combine(vaultDirectory, "vault.key");
            var databasePath = Path.Combine(vaultDirectory, "vault.db");
            await VaultKeyEnvelopeSerializer.WriteAtomicAsync(
                keyPath,
                creation.Envelope,
                CancellationToken.None);

            var migrations = DatabaseMigrationCatalog.Load()
                .Take(schemaVersion)
                .ToArray();
            if (schemaVersion > DatabaseMigrationCatalog.CurrentVersion)
            {
                migrations =
                [
                    .. migrations,
                    new DatabaseMigration(
                        schemaVersion,
                        "compatibility_probe",
                        "CREATE TABLE compatibility_probe(id INTEGER PRIMARY KEY) STRICT;",
                        new string('a', 64))
                ];
            }

            await using (var session =
                         await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                             vaultDirectory,
                             material,
                             create: true))
            {
                await DatabaseInitializer.EnsureSchemaAsync(
                    session.Connection,
                    material.VaultId,
                    DateTimeOffset.UtcNow,
                    migrations,
                    CancellationToken.None);
            }

            if (corruptMigrationHistory)
            {
                await using var session =
                    await SqliteMcDatabaseFactory.OpenSessionAsync(
                        vaultDirectory,
                        material,
                        create: false);
                await using var command = session.Connection.CreateCommand();
                command.CommandText =
                    "UPDATE schema_migrations SET checksum_sha256 = $checksum WHERE version = 5;";
                command.Parameters.AddWithValue(
                    "$checksum",
                    new string('0', 64));
                await command.ExecuteNonQueryAsync();
            }

            if (corruptDatabase)
            {
                await using var session =
                    await SqliteMcDatabaseFactory.OpenSessionAsync(
                        vaultDirectory,
                        material,
                        create: false);
                await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                    session.Connection,
                    """
                    PRAGMA writable_schema = ON;
                    UPDATE sqlite_master SET rootpage = 0 WHERE name = 'documents';
                    PRAGMA writable_schema = OFF;
                    """,
                    CancellationToken.None);
            }

            await BackupPackageWriter.WriteAsync(
                packagePath,
                material.VaultId,
                DateTimeOffset.UtcNow,
                databasePath,
                keyPath,
                CancellationToken.None);
            return new BackupFixture(root, packagePath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
