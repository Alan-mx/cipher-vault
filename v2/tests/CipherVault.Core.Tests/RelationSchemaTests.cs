using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Tests;

public sealed class RelationSchemaTests
{
    private static readonly Guid SourceId = Guid.Parse(
        "00000000-0000-0000-0000-000000000001");
    private static readonly Guid TargetId = Guid.Parse(
        "00000000-0000-0000-0000-000000000002");
    private const string Now = "2026-11-05T00:00:00Z";

    [Fact]
    public async Task SuggestionConstraintsEnforcePendingRevisionAndEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        await InsertDocumentsAsync(fixture);
        var first = Guid.NewGuid();

        await InsertSuggestionAsync(
            fixture,
            first,
            "related",
            SourceId,
            TargetId);
        await AssertConstraintAsync(
            fixture,
            () => InsertSuggestionAsync(
                fixture,
                Guid.NewGuid(),
                "related",
                TargetId,
                SourceId),
            "UNIQUE");
        await AssertConstraintAsync(
            fixture,
            () => InsertSuggestionAsync(
                fixture,
                Guid.NewGuid(),
                "depends_on",
                SourceId,
                TargetId,
                modelName: null),
            "CHECK");
        await AssertConstraintAsync(
            fixture,
            () => InsertSuggestionAsync(
                fixture,
                Guid.NewGuid(),
                "depends_on",
                SourceId,
                TargetId,
                evidenceJson: "[{},{},{},{}]"),
            "CHECK");
        await AssertConstraintAsync(
            fixture,
            () => ExecuteAsync(
                fixture,
                $"""
                 UPDATE document_relation_suggestions
                 SET reason = 'changed'
                 WHERE suggestion_id = '{first:D}';
                 """),
            "revision");

        await ExecuteAsync(
            fixture,
            $"""
             UPDATE document_relation_suggestions
             SET suggestion_revision = 2,
                 status = 'stale',
                 stale_reason_code = 'source_revision_changed',
                 updated_at = '{Now}'
             WHERE suggestion_id = '{first:D}';

             UPDATE document_relation_suggestions
             SET suggestion_revision = 3,
                 status = 'pending_review',
                 stale_reason_code = NULL,
                 updated_at = '{Now}'
             WHERE suggestion_id = '{first:D}';
             """);
        await AssertConstraintAsync(
            fixture,
            () => ExecuteAsync(
                fixture,
                $"""
                 UPDATE document_relation_suggestions
                 SET suggestion_revision = 4,
                     status = 'applied',
                     terminal_at = '{Now}',
                     applied_at = '{Now}'
                 WHERE suggestion_id = '{first:D}';
                 """),
            "CHECK");
    }

    [Fact]
    public async Task DocumentRelationsNormalizeAndAllowRecreateAfterRetract()
    {
        await using var fixture = await Fixture.CreateAsync();
        await InsertDocumentsAsync(fixture);
        var relationId = Guid.NewGuid();

        await ExecuteAsync(
            fixture,
            $"""
             INSERT INTO document_relations(
                 relation_id, relation_type, source_document_id,
                 target_document_id, status, created_by_actor,
                 created_at, updated_at)
             VALUES(
                 '{relationId:D}', 'related', '{SourceId:D}', '{TargetId:D}',
                 'active', 'user_ui', '{Now}', '{Now}');
             """);
        await AssertConstraintAsync(
            fixture,
            () => ExecuteAsync(
                fixture,
                $"""
                 INSERT INTO document_relations(
                     relation_id, relation_type, source_document_id,
                     target_document_id, status, created_by_actor,
                     created_at, updated_at)
                 VALUES(
                     '{Guid.NewGuid():D}', 'related',
                     '{TargetId:D}', '{SourceId:D}',
                     'active', 'user_ui', '{Now}', '{Now}');
                 """),
            "CHECK");
        await AssertConstraintAsync(
            fixture,
            () => ExecuteAsync(
                fixture,
                $"""
                 INSERT INTO document_relations(
                     relation_id, relation_type, source_document_id,
                     target_document_id, status, created_by_actor,
                     created_at, updated_at)
                 VALUES(
                     '{Guid.NewGuid():D}', 'related',
                     '{SourceId:D}', '{TargetId:D}',
                     'active', 'user_ui', '{Now}', '{Now}');
                 """),
            "UNIQUE");

        await ExecuteAsync(
            fixture,
            $"""
             UPDATE document_relations
             SET status = 'retracted',
                 retracted_at = '2026-11-05T00:00:01Z',
                 retracted_by_actor = 'user_ui',
                 updated_at = '2026-11-05T00:00:01Z'
             WHERE relation_id = '{relationId:D}';
             """);
        await AssertConstraintAsync(
            fixture,
            () => ExecuteAsync(
                fixture,
                $"""
                 UPDATE document_relations
                 SET retracted_at = '2026-11-05T00:00:02Z',
                     updated_at = '2026-11-05T00:00:02Z'
                 WHERE relation_id = '{relationId:D}';
                 """),
            "terminal");

        await ExecuteAsync(
            fixture,
            $"""
             INSERT INTO document_relations(
                 relation_id, relation_type, source_document_id,
                 target_document_id, status, created_by_actor,
                 created_at, updated_at)
             VALUES(
                 '{Guid.NewGuid():D}', 'related',
                 '{SourceId:D}', '{TargetId:D}',
                 'active', 'user_ui', '{Now}', '{Now}');

             INSERT INTO document_relations(
                 relation_id, relation_type, source_document_id,
                 target_document_id, status, created_by_actor,
                 created_at, updated_at)
             VALUES(
                 '{Guid.NewGuid():D}', 'depends_on',
                 '{SourceId:D}', '{TargetId:D}',
                 'active', 'user_ui', '{Now}', '{Now}');

             INSERT INTO document_relations(
                 relation_id, relation_type, source_document_id,
                 target_document_id, status, created_by_actor,
                 created_at, updated_at)
             VALUES(
                 '{Guid.NewGuid():D}', 'depends_on',
                 '{TargetId:D}', '{SourceId:D}',
                 'active', 'user_ui', '{Now}', '{Now}');
             """);
    }

    [Fact]
    public async Task IdempotencyNamespaceAuditWhitelistAndCascadeAreActive()
    {
        await using var fixture = await Fixture.CreateAsync();
        await InsertDocumentsAsync(fixture);
        var key = Guid.NewGuid();
        var relationId = Guid.NewGuid();
        var suggestionId = Guid.NewGuid();

        await ExecuteAsync(
            fixture,
            $"""
             INSERT INTO idempotency_records(
                 actor_kind, operation, idempotency_key, session_id,
                 request_hash, result_json, created_at, expires_at)
             VALUES
                 ('user_ui', 'relation_suggestion.create', '{key:D}', NULL,
                  '{new string('a', 64)}', json_object(), '{Now}', '{Now}'),
                 ('mcp', 'relation_suggestion.create', '{key:D}', NULL,
                  '{new string('b', 64)}', json_object(), '{Now}', '{Now}'),
                 ('user_ui', 'document_relation.retract', '{key:D}', NULL,
                  '{new string('c', 64)}', json_object(), '{Now}', '{Now}');

             INSERT INTO document_relations(
                 relation_id, relation_type, source_document_id,
                 target_document_id, status, created_by_actor,
                 created_at, updated_at)
             VALUES(
                 '{relationId:D}', 'depends_on', '{SourceId:D}', '{TargetId:D}',
                 'active', 'user_ui', '{Now}', '{Now}');
             """);
        await InsertSuggestionAsync(
            fixture,
            suggestionId,
            "depends_on",
            SourceId,
            TargetId);

        await ExecuteAsync(
            fixture,
            $"""
             INSERT INTO audit_events(
                 event_id, occurred_at, actor_kind, action, document_id,
                 revision, correlation_id, result, details_json)
             VALUES(
                 '{Guid.NewGuid():D}', '{Now}', 'user_ui',
                 'document_relation.retract', '{SourceId:D}', NULL, NULL,
                 'success', json_object('relationId', '{relationId:D}')),
                (
                 '{Guid.NewGuid():D}', '{Now}', 'system',
                 'relation_suggestion.cleanup', NULL, NULL, NULL,
                 'success', json_object('deletedSuggestions', 1));
             """);
        await AssertConstraintAsync(
            fixture,
            () => ExecuteAsync(
                fixture,
                $"""
                 INSERT INTO audit_events(
                     event_id, occurred_at, actor_kind, action, document_id,
                     revision, correlation_id, result, details_json)
                 VALUES(
                     '{Guid.NewGuid():D}', '{Now}', 'user_ui',
                     'relation_suggestion.invalid', NULL, NULL, NULL,
                     'success', json_object());
                 """),
            "CHECK");

        await ExecuteAsync(
            fixture,
            $"DELETE FROM documents WHERE document_id = '{TargetId:D}';");
        Assert.Equal(
            0,
            await CountAsync(
                fixture,
                "SELECT count(*) FROM document_relations;"));
        Assert.Equal(
            0,
            await CountAsync(
                fixture,
                "SELECT count(*) FROM document_relation_suggestions;"));
        Assert.Equal(
            2,
            await CountAsync(
                fixture,
                "SELECT count(*) FROM audit_events;"));
        Assert.Equal(
            0,
            await CountAsync(
                fixture,
                "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    private static Task InsertDocumentsAsync(Fixture fixture) =>
        ExecuteAsync(
            fixture,
            $"""
             INSERT INTO documents(
                 document_id, title, title_key, path, path_key, format,
                 content, frontmatter_json, headings_json, revision,
                 created_at, updated_at)
             VALUES
                 (
                     '{SourceId:D}', 'Source', 'source', '/source.md',
                     '/source.md', 'md', '# Source', json_object(), '[]', 1,
                     '{Now}', '{Now}'
                 ),
                 (
                     '{TargetId:D}', 'Target', 'target', '/target.md',
                     '/target.md', 'md', '# Target', json_object(), '[]', 1,
                     '{Now}', '{Now}'
                 );
             """);

    private static Task InsertSuggestionAsync(
        Fixture fixture,
        Guid suggestionId,
        string relationType,
        Guid sourceId,
        Guid targetId,
        string? modelName = "test-model",
        string evidenceJson = "[]") =>
        ExecuteAsync(
            fixture,
            $"""
             INSERT INTO document_relation_suggestions(
                 suggestion_id, suggestion_revision, payload_version,
                 relation_type, source_document_id, source_document_revision,
                 target_document_id, target_document_revision, reason,
                 model_name, evidence_json, status, source_actor,
                 created_at, updated_at)
             VALUES(
                 '{suggestionId:D}', 1, 1, '{relationType}',
                 '{sourceId:D}', 1, '{targetId:D}', 1, 'reason',
                 {(modelName is null ? "NULL" : $"'{modelName}'")},
                 '{evidenceJson}', 'pending_review', 'mcp',
                 '{Now}', '{Now}');
             """);

    private static async Task ExecuteAsync(Fixture fixture, string sql)
    {
        using var command = fixture.Session.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertConstraintAsync(
        Fixture fixture,
        Func<Task> action,
        string message)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(action);
        Assert.Contains(
            message,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> CountAsync(Fixture fixture, string sql)
    {
        using var command = fixture.Session.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            VaultKeyMaterial material,
            SqliteMcDatabaseSession session)
        {
            Root = root;
            Material = material;
            Session = session;
        }

        public string Root { get; }

        public VaultKeyMaterial Material { get; }

        public SqliteMcDatabaseSession Session { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-relation-schema-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            VaultFilePermissions.ProtectDirectory(root);
            var material = new VaultKeyMaterial(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32));
            await SqliteMcVaultStore.InitializeAsync(root, material);
            var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
                root,
                material,
                create: false);
            return new Fixture(root, material, session);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Material.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
