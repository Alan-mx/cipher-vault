using System.Security.Cryptography;
using System.Text;
using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Security;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Tests;

public sealed class BackupCompatibilityFixtureTests
{
    [Fact]
    public void SafeSuccessOutputIncludesMigrationState()
    {
        var output = InteractiveConsoleTerminal.FormatSafeSuccess(
            new RestoreVerificationResult(
                Guid.NewGuid(),
                5,
                1,
                100,
                20,
                5,
                5,
                false,
                null));

        Assert.Contains(
            "schemaVersion=5 sourceSchemaVersion=5 currentSchemaVersion=5",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "schemaMigrationPending=false migrationTrigger=null",
            output,
            StringComparison.Ordinal);

        var pendingOutput = InteractiveConsoleTerminal.FormatSafeSuccess(
            new RestoreVerificationResult(
                Guid.NewGuid(),
                5,
                1,
                100,
                20,
                5,
                6,
                true,
                BackupMigrationTriggers.FirstUnlock));
        Assert.Contains(
            "sourceSchemaVersion=5 currentSchemaVersion=6 schemaMigrationPending=true migrationTrigger=first_unlock",
            pendingOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreSchemaFiveFixturePreservesDataAndUnlocks()
    {
        var metadata = await BackupCompatibilityFixture.ReadMetadataAsync(
            CancellationToken.None);
        await BackupCompatibilityFixture.VerifyPackageIdentityAsync(
            metadata,
            CancellationToken.None);
        var fixtureHashBefore = await HashFileAsync(
            BackupCompatibilityFixture.PackagePath);

        var external = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        try
        {
            var restoreRoot = Path.Combine(external, "restore-context");
            Directory.CreateDirectory(restoreRoot);
            var paths = ApplicationPaths.Create(
                Path.Combine(restoreRoot, "data"),
                Path.Combine(restoreRoot, "config"),
                Path.Combine(restoreRoot, "backups"));
            var target = Path.Combine(restoreRoot, "restored");
            var options = new ServerOptions(
                paths,
                [new ServerListenAddress("127.0.0.1", 43133)],
                TimeSpan.FromMinutes(10),
                ServerOptions.DefaultMaxRequestBodyBytes);
            var terminal = new CapturingInteractiveConsole(
                metadata.TestPassword);

            var exitCode = await new RestoreMaintenanceCommand().RunAsync(
                options,
                new RestoreCommandOptions(
                    BackupCompatibilityFixture.PackagePath,
                    target),
                terminal,
                CancellationToken.None);

            Assert.True(
                exitCode == RestoreExitCodes.Success,
                terminal.LastError?.SafeMessage);
            var restoreResult = terminal.LastSuccess;
            Assert.NotNull(restoreResult);
            Assert.Equal(5, restoreResult.SchemaVersion);
            Assert.Equal(5, restoreResult.SourceSchemaVersion);
            Assert.Equal(8, restoreResult.CurrentSchemaVersion);
            Assert.True(restoreResult.SchemaMigrationPending);
            Assert.Equal(
                BackupMigrationTriggers.FirstUnlock,
                restoreResult.MigrationTrigger);
            Assert.True(File.Exists(Path.Combine(target, "vault.db")));
            Assert.True(File.Exists(Path.Combine(target, "vault.key")));

            using var material = await VaultKeyService.UnlockAsync(
                Path.Combine(target, "vault.key"),
                metadata.TestPassword);
            await using (var session =
                         await SqliteMcDatabaseFactory.OpenSessionAsync(
                             target,
                             material,
                             create: false))
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = """
                    SELECT d.title, d.path, d.revision, d.content,
                           (
                               SELECT group_concat(t.name, ',')
                               FROM document_tags dt
                               JOIN tags t ON t.tag_id = dt.tag_id
                               WHERE dt.document_id = d.document_id
                           )
                    FROM documents d
                    WHERE d.path = 'fixtures/compatibility-fixture.md';
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(
                    metadata.Documents[0].Title,
                    reader.GetString(0));
                Assert.Equal(metadata.Documents[0].Path, reader.GetString(1));
                Assert.Equal(metadata.Documents[0].Revision, reader.GetInt32(2));
                var content = reader.GetString(3);
                var contentHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                    .ToLowerInvariant();
                Assert.Equal(metadata.Documents[0].ContentSha256, contentHash);
                Assert.Equal(
                    metadata.Documents[0].Tags.ToHashSet(StringComparer.Ordinal),
                    reader.GetString(4)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .ToHashSet(StringComparer.Ordinal));
            }

            await using var manager = await VaultSessionManager.CreateAsync(
                target);
            Assert.Equal(VaultSessionState.Locked, manager.State);
            var status = await manager.UnlockAsync(metadata.TestPassword);
            Assert.Equal(VaultSessionState.Unlocked, status.State);
            var upgradedSchema = await manager.ExecuteAsync(
                async (context, token) =>
                {
                    using var command = context.CreateCommand();
                    command.Command.CommandText =
                        """
                        SELECT
                            (SELECT value FROM vault_meta WHERE key = 'schema_version'),
                            (SELECT count(*) FROM sqlite_master
                             WHERE name IN (
                                 'document_drafts',
                                 'idempotency_records',
                                 'semantic_profiles',
                                 'semantic_remote_consents',
                                 'semantic_index_jobs',
                                 'semantic_chunks'
                             ));
                        """;
                    await using var reader =
                        await command.Command.ExecuteReaderAsync(token);
                    Assert.True(await reader.ReadAsync(token));
                    return (reader.GetString(0), reader.GetInt32(1));
                });
            Assert.Equal(("8", 6), upgradedSchema);
            Assert.Equal(
                fixtureHashBefore,
                await HashFileAsync(BackupCompatibilityFixture.PackagePath));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaFiveFixtureUpgradesWithPublishedMigrations()
    {
        var metadata = await BackupCompatibilityFixture.ReadMetadataAsync(
            CancellationToken.None);
        var external = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-compat-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        try
        {
            var staging = Path.Combine(external, "payload");
            Directory.CreateDirectory(staging);
            using (var credential =
                   new InteractivePasswordCredential(metadata.TestPassword))
            {
                var restore = await BackupRestoreStore.ExtractAndValidateAsync(
                    BackupCompatibilityFixture.PackagePath,
                    staging,
                    credential,
                    CancellationToken.None);
                Assert.Equal(5, restore.SchemaVersion);
            }

            using var material = await VaultKeyService.UnlockAsync(
                Path.Combine(staging, "vault.key"),
                metadata.TestPassword);
            await using var session =
                await SqliteMcDatabaseFactory.OpenSessionAsync(
                    staging,
                    material,
                    create: false);
            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                DatabaseMigrationCatalog.Load(),
                CancellationToken.None);

            Assert.Equal(8, upgraded.SchemaVersion);
            using var command = session.Connection.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM documents WHERE path = 'fixtures/compatibility-fixture.md';";
            Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        var hash = SHA256.HashData(
            await File.ReadAllBytesAsync(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class CapturingInteractiveConsole : IInteractiveConsole
    {
        private readonly string password;

        public CapturingInteractiveConsole(string password)
        {
            this.password = password;
        }

        public bool IsInteractive => true;

        public RestoreOutcome? LastError { get; private set; }

        public RestoreVerificationResult? LastSuccess { get; private set; }

        public Task<string?> ReadPasswordAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(password);

        public void WriteSafeError(RestoreOutcome outcome)
        {
            LastError = outcome;
        }

        public void WriteSafeSuccess(RestoreVerificationResult result)
        {
            LastSuccess = result;
        }
    }
}
