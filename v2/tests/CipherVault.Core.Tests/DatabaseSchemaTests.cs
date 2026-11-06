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
    public async Task InitializeCreatesMcpFirstBaseline()
    {
        using var fixture = await Fixture.CreateAsync();

        var initialized = await SqliteMcVaultStore.InitializeAsync(
            fixture.Root,
            fixture.Material);
        var verified = await SqliteMcVaultStore.VerifyAsync(
            fixture.Root,
            fixture.Material);

        Assert.Equal(1, initialized.SchemaVersion);
        Assert.Equal(initialized, verified);

        await using var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false);
        Assert.Equal(
            "mcp_first_baseline",
            await SqliteMcDatabaseFactory.ExecuteScalarStringAsync(
                session.Connection,
                "SELECT name FROM schema_migrations WHERE version = 1;",
                CancellationToken.None));
        Assert.Equal(
            "1",
            await SqliteMcDatabaseFactory.ExecuteScalarStringAsync(
                session.Connection,
                "SELECT value FROM vault_meta WHERE key = 'schema_version';",
                CancellationToken.None));
        Assert.Equal(
            6,
            await CountAsync(
                session.Connection,
                "SELECT count(*) FROM knowledge_types;"));
        Assert.Equal(
            10,
            await CountAsync(
                session.Connection,
                "SELECT count(*) FROM relation_types;"));
        Assert.Equal(
            5,
            await CountAsync(
                session.Connection,
                "SELECT count(*) FROM source_rules;"));
    }

    [Fact]
    public async Task InitializationIsIdempotent()
    {
        using var fixture = await Fixture.CreateAsync();

        _ = await SqliteMcVaultStore.InitializeAsync(
            fixture.Root,
            fixture.Material);
        var second = await SqliteMcVaultStore.InitializeAsync(
            fixture.Root,
            fixture.Material);

        Assert.Equal(1, second.SchemaVersion);
        await using var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false);
        Assert.Equal(
            1,
            await CountAsync(
                session.Connection,
                "SELECT count(*) FROM schema_migrations;"));
    }

    [Fact]
    public async Task BaselineSupportsKnowledgeSourceAndFtsRoundTrip()
    {
        using var fixture = await Fixture.CreateAsync();
        _ = await SqliteMcVaultStore.InitializeAsync(
            fixture.Root,
            fixture.Material);
        await using var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false);

        var now = "2026-11-05T00:00:00.0000000Z";
        var sourceId = Guid.NewGuid().ToString("D");
        var sourceRevisionId = Guid.NewGuid().ToString("D");
        var locatorId = Guid.NewGuid().ToString("D");
        var knowledgeId = Guid.NewGuid().ToString("D");
        var knowledgeRevisionId = Guid.NewGuid().ToString("D");
        var sourceReferenceId = Guid.NewGuid().ToString("D");
        var noteTypeId = "00000000-0000-4000-8000-000000001001";
        var contentHash = new string('a', 64);

        await using var insert = session.Connection.CreateCommand();
        insert.CommandText =
            """
            BEGIN;

            INSERT INTO sources(
                source_id, source_kind, title, title_key, metadata_json,
                status, current_revision_id, created_at, updated_at, archived_at)
            VALUES(
                $sourceId, 'web_page', 'Source', 'SOURCE',
                '{"url":"https://example.com"}', 'active',
                $sourceRevisionId, $now, $now, NULL);

            INSERT INTO source_revisions(
                source_revision_id, source_id, revision, source_kind, title,
                metadata_json, content_format, content_text, content_hash,
                knowledge_model_version, locator_index_version, created_at)
            VALUES(
                $sourceRevisionId, $sourceId, 1, 'web_page', 'Source',
                '{"url":"https://example.com"}', 'markdown',
                '# Section' || char(10) || char(10) || 'Paragraph',
                $contentHash, 1, 1, $now);

            INSERT INTO source_locators(
                source_locator_id, source_revision_id, locator_type,
                locator_key, locator_value_json, label, start_offset,
                end_offset, ordinal, parent_locator_id)
            VALUES(
                $locatorId, $sourceRevisionId, 'section', '["Section"]',
                '["Section"]', 'Section', 0, 21, 0, NULL);

            INSERT INTO knowledge_items(
                knowledge_id, title, title_key, summary, knowledge_type_id,
                status, current_revision_id, created_at, updated_at, archived_at)
            VALUES(
                $knowledgeId, 'Knowledge', 'KNOWLEDGE', 'Summary',
                $noteTypeId, 'active', $knowledgeRevisionId, $now, $now, NULL);

            INSERT INTO knowledge_revisions(
                knowledge_revision_id, knowledge_id, revision,
                knowledge_type_id, template_id, knowledge_model_version,
                title, summary, content_markdown, content_hash,
                properties_json, tags_json, created_at)
            VALUES(
                $knowledgeRevisionId, $knowledgeId, 1, $noteTypeId, NULL, 1,
                'Knowledge', 'Summary', '# Knowledge', $contentHash,
                '{}', '[]', $now);

            INSERT INTO knowledge_tags(knowledge_id, tag_key, tag_label)
            VALUES($knowledgeId, 'DEMO', 'demo');

            INSERT INTO source_references(
                source_reference_id, source_reference_order,
                knowledge_revision_id, source_id,
                source_revision_id, source_revision, source_locator_id,
                locator_type, locator_key, locator_value_json, excerpt,
                extraction_metadata_json, created_at)
            VALUES(
                $sourceReferenceId, 0, $knowledgeRevisionId, $sourceId,
                $sourceRevisionId, 1, $locatorId, 'section',
                '["Section"]', '["Section"]', 'Paragraph', '{}', $now);

            INSERT INTO knowledge_fts(
                knowledge_id, title, summary, type_key, tags, content)
            VALUES(
                $knowledgeId, 'Knowledge', 'Summary', 'note', 'demo',
                '# Knowledge');

            COMMIT;
            """;
        insert.Parameters.AddWithValue("$sourceId", sourceId);
        insert.Parameters.AddWithValue("$sourceRevisionId", sourceRevisionId);
        insert.Parameters.AddWithValue("$locatorId", locatorId);
        insert.Parameters.AddWithValue("$knowledgeId", knowledgeId);
        insert.Parameters.AddWithValue(
            "$knowledgeRevisionId",
            knowledgeRevisionId);
        insert.Parameters.AddWithValue("$sourceReferenceId", sourceReferenceId);
        insert.Parameters.AddWithValue("$noteTypeId", noteTypeId);
        insert.Parameters.AddWithValue("$contentHash", contentHash);
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync();

        Assert.Equal(
            1,
            await CountAsync(
                session.Connection,
                "SELECT count(*) FROM source_references;"));
        Assert.Equal(
            1,
            await CountAsync(
                session.Connection,
                """
                SELECT count(*) FROM knowledge_fts
                WHERE knowledge_fts MATCH '"Knowledge"';
                """));
        Assert.Equal(
            "ok",
            await SqliteMcDatabaseFactory.ExecuteScalarStringAsync(
                session.Connection,
                "PRAGMA integrity_check;",
                CancellationToken.None));
    }

    [Fact]
    public async Task HigherSchemaVersionIsRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        _ = await SqliteMcVaultStore.InitializeAsync(
            fixture.Root,
            fixture.Material);

        await using (var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false))
        {
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                """
                INSERT INTO schema_migrations(
                    version, name, checksum_sha256, applied_at)
                VALUES(2, 'future', lower(hex(randomblob(32))), '2026-11-05T00:00:00Z');
                UPDATE vault_meta SET value = '2' WHERE key = 'schema_version';
                """,
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            SqliteMcVaultStore.VerifyAsync(
                fixture.Root,
                fixture.Material));
        Assert.Equal(VaultErrorCode.UnsupportedVersion, exception.Code);
    }

    [Fact]
    public async Task FailedMigrationRollsBackAllSchemaChanges()
    {
        using var fixture = await Fixture.CreateAsync();
        await using var session =
            await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                fixture.Root,
                fixture.Material,
                create: true);
        var badMigration = new DatabaseMigration(
            1,
            "bad",
            """
            CREATE TABLE vault_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
            INSERT INTO missing_table VALUES(1);
            """,
            new string('0', 64),
            []);

        await Assert.ThrowsAnyAsync<SqliteException>(() =>
            DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                fixture.Material.VaultId,
                DateTimeOffset.UtcNow,
                [badMigration],
                CancellationToken.None));

        Assert.Equal(
            0,
            await CountAsync(
                session.Connection,
                """
                SELECT count(*) FROM sqlite_master
                WHERE name IN ('vault_meta', 'schema_migrations');
                """));
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-schema-{Guid.NewGuid():N}");
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
