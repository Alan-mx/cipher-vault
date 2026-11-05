using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Core.Storage.KnowledgeBase;

public static class DashboardStore
{
    public static async Task<DashboardView> GetAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        var knowledgePage = await KnowledgeStore.ListAsync(
            context,
            new KnowledgeQuery(null, null, null, 5, null),
            cancellationToken);
        var sourcePage = await SourceStore.ListAsync(
            context,
            new SourceQuery(null, null, null, 5, null),
            cancellationToken);
        var pending = await UnifiedDraftStore.ListAsync(
            context,
            new DraftQuery(
                DraftStatus.PendingReview,
                null,
                null,
                null,
                null,
                null,
                null,
                5,
                null),
            cancellationToken);
        var inbox = await UnifiedDraftStore.ListAsync(
            context,
            new DraftQuery(
                DraftStatus.Draft,
                DraftTargetObjectType.Knowledge,
                null,
                null,
                null,
                null,
                null,
                5,
                null),
            cancellationToken);
        var relations = await ReadRecentRelationsAsync(
            context,
            cancellationToken);
        var counts = await ReadCountsAsync(context, cancellationToken);
        return new DashboardView(
            knowledgePage.Items,
            sourcePage.Items,
            pending.Items,
            inbox.Items,
            relations,
            counts);
    }

    private static async Task<IReadOnlyList<KnowledgeRelationItem>>
        ReadRecentRelationsAsync(
            VaultOperationContext context,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                r.relation_id,
                r.relation_type_id,
                t.relation_key,
                r.source_knowledge_id,
                r.target_knowledge_id,
                source_r.revision,
                target_r.revision,
                r.status,
                rv.rationale,
                r.created_at,
                r.updated_at
            FROM knowledge_relations AS r
            JOIN relation_types AS t ON t.relation_type_id = r.relation_type_id
            JOIN knowledge_relation_revisions AS rv
              ON rv.relation_revision_id = r.current_revision_id
            JOIN knowledge_revisions AS source_r
              ON source_r.knowledge_revision_id =
                 rv.source_knowledge_revision_id
            JOIN knowledge_revisions AS target_r
              ON target_r.knowledge_revision_id =
                 rv.target_knowledge_revision_id
            ORDER BY r.updated_at DESC, r.relation_id DESC
            LIMIT 5;
            """;
        var result = new List<KnowledgeRelationItem>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new KnowledgeRelationItem(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                Guid.Parse(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7) == "active"
                    ? KnowledgeRelationStatus.Active
                    : KnowledgeRelationStatus.Retracted,
                reader.GetString(8),
                ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10))));
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadCountsAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                (SELECT count(*) FROM knowledge_items WHERE status = 'active'),
                (SELECT count(*) FROM sources WHERE status = 'active'),
                (SELECT count(*) FROM drafts WHERE status = 'pending_review'),
                (SELECT count(*) FROM drafts
                 WHERE status = 'draft' AND target_object_type = 'knowledge');
            """;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["knowledge"] = reader.GetInt32(0),
            ["sources"] = reader.GetInt32(1),
            ["pendingReview"] = reader.GetInt32(2),
            ["inbox"] = reader.GetInt32(3)
        };
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
