using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Relations;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Relations;

public static class DocumentRelationStore
{
    private const int MaxDocumentRelations = 200;

    public static async Task<DocumentRelation?> GetActiveAsync(
        VaultOperationContext context,
        Guid relationId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectColumns}
             FROM document_relations
             WHERE relation_id = $relationId
               AND status = 'active';
             """;
        command.Command.Parameters.AddWithValue(
            "$relationId",
            relationId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRelation(reader)
            : null;
    }

    public static async Task<(DocumentRelation Relation, bool Inserted)>
        EnsureActiveAsync(
            VaultOperationContext context,
            RelationType relationType,
            Guid sourceDocumentId,
            Guid targetDocumentId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var canonical = RelationDirection.Canonicalize(
            relationType,
            sourceDocumentId,
            targetDocumentId);
        var existing = await FindActiveEdgeAsync(
            context,
            relationType,
            canonical.SourceDocumentId,
            canonical.TargetDocumentId,
            cancellationToken);
        if (existing is not null)
        {
            return (existing, false);
        }

        var relation = new DocumentRelation(
            Guid.NewGuid(),
            relationType,
            canonical.SourceDocumentId,
            canonical.TargetDocumentId,
            DocumentRelationStatus.Active,
            "user_ui",
            now,
            now,
            null,
            null);
        try
        {
            using var command = context.CreateCommand();
            command.Command.CommandText =
                """
                INSERT INTO document_relations(
                    relation_id,
                    relation_type,
                    source_document_id,
                    target_document_id,
                    status,
                    created_by_actor,
                    created_at,
                    updated_at,
                    retracted_at,
                    retracted_by_actor)
                VALUES(
                    $relationId,
                    $relationType,
                    $sourceDocumentId,
                    $targetDocumentId,
                    $status,
                    $createdByActor,
                    $createdAt,
                    $updatedAt,
                    NULL,
                    NULL);
                """;
            AddRelationParameters(command.Command, relation);
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
            return (relation, true);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19
                && exception.Message.Contains(
                    "UNIQUE",
                    StringComparison.OrdinalIgnoreCase))
        {
            var raced = await FindActiveEdgeAsync(
                context,
                relationType,
                canonical.SourceDocumentId,
                canonical.TargetDocumentId,
                cancellationToken);
            return raced is null
                ? throw new VaultException(
                    VaultErrorCode.DatabaseError,
                    "The active document relation could not be read after a uniqueness conflict.")
                : (raced, false);
        }
    }

    public static async Task<DocumentRelation> RetractAsync(
        VaultOperationContext context,
        Guid relationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                UPDATE document_relations
                SET status = 'retracted',
                    updated_at = $updatedAt,
                    retracted_at = $updatedAt,
                    retracted_by_actor = 'user_ui'
                WHERE relation_id = $relationId
                  AND status = 'active';
                """;
            command.Command.Parameters.AddWithValue(
                "$updatedAt",
                now.ToUniversalTime().ToString("O"));
            command.Command.Parameters.AddWithValue(
                "$relationId",
                relationId.ToString("D"));
            if (await command.Command.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new VaultException(
                    VaultErrorCode.DocumentRelationNotFound,
                    "Active document relation was not found.");
            }
        }

        using var read = context.CreateCommand();
        read.Command.CommandText =
            $"""
             {SelectColumns}
             FROM document_relations
             WHERE relation_id = $relationId
               AND status = 'retracted';
             """;
        read.Command.Parameters.AddWithValue(
            "$relationId",
            relationId.ToString("D"));
        await using var reader =
            await read.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRelation(reader)
            : throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Retracted document relation could not be read.");
    }

    public static async Task<DocumentRelationsView> ListForDocumentAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                r.relation_id,
                r.relation_type,
                r.source_document_id,
                r.target_document_id,
                r.created_at,
                r.updated_at,
                other.document_id,
                other.title,
                other.path,
                other.revision
            FROM document_relations AS r
            JOIN documents AS source
              ON source.document_id = r.source_document_id
            JOIN documents AS target
              ON target.document_id = r.target_document_id
            JOIN documents AS other
              ON other.document_id = CASE
                  WHEN r.source_document_id = $documentId
                      THEN r.target_document_id
                  ELSE r.source_document_id
              END
            WHERE r.status = 'active'
              AND (r.source_document_id = $documentId
                   OR r.target_document_id = $documentId)
              AND source.deleted_at IS NULL
              AND target.deleted_at IS NULL
            ORDER BY r.updated_at DESC, r.relation_id DESC
            LIMIT $limit;
            """;
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$limit",
            MaxDocumentRelations + 1);
        var items = new List<DocumentRelationItem>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var relationType = RelationValueMapping.ParseRelationType(
                reader.GetString(1));
            var sourceId = Guid.Parse(reader.GetString(2));
            var targetId = Guid.Parse(reader.GetString(3));
            var direction = relationType.IsDirected()
                ? sourceId == documentId
                    ? DocumentRelationDirection.Outgoing
                    : DocumentRelationDirection.Incoming
                : DocumentRelationDirection.Symmetric;
            items.Add(new DocumentRelationItem(
                Guid.Parse(reader.GetString(0)),
                relationType,
                relationType.IsDirected(),
                direction,
                new RelationDocumentReference(
                    Guid.Parse(reader.GetString(6)),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt32(9)),
                ParseTimestamp(reader.GetString(4)),
                ParseTimestamp(reader.GetString(5))));
        }
        var truncated = items.Count > MaxDocumentRelations;
        if (truncated)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new DocumentRelationsView(documentId, items, truncated);
    }

    private static async Task<DocumentRelation?> FindActiveEdgeAsync(
        VaultOperationContext context,
        RelationType relationType,
        Guid sourceDocumentId,
        Guid targetDocumentId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectColumns}
             FROM document_relations
             WHERE relation_type = $relationType
               AND source_document_id = $sourceDocumentId
               AND target_document_id = $targetDocumentId
               AND status = 'active'
             LIMIT 1;
             """;
        command.Command.Parameters.AddWithValue(
            "$relationType",
            relationType.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$sourceDocumentId",
            sourceDocumentId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$targetDocumentId",
            targetDocumentId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRelation(reader)
            : null;
    }

    private static void AddRelationParameters(
        SqliteCommand command,
        DocumentRelation relation)
    {
        command.Parameters.AddWithValue(
            "$relationId",
            relation.RelationId.ToString("D"));
        command.Parameters.AddWithValue(
            "$relationType",
            relation.RelationType.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$sourceDocumentId",
            relation.SourceDocumentId.ToString("D"));
        command.Parameters.AddWithValue(
            "$targetDocumentId",
            relation.TargetDocumentId.ToString("D"));
        command.Parameters.AddWithValue(
            "$status",
            relation.Status.ToDatabaseValue());
        command.Parameters.AddWithValue(
            "$createdByActor",
            relation.CreatedByActor);
        command.Parameters.AddWithValue(
            "$createdAt",
            relation.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$updatedAt",
            relation.UpdatedAt.ToUniversalTime().ToString("O"));
    }

    private static DocumentRelation ReadRelation(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            RelationValueMapping.ParseRelationType(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            RelationValueMapping.ParseDocumentRelationStatus(
                reader.GetString(4)),
            reader.GetString(5),
            ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private const string SelectColumns =
        """
        SELECT
            relation_id,
            relation_type,
            source_document_id,
            target_document_id,
            status,
            created_by_actor,
            created_at,
            updated_at,
            retracted_at,
            retracted_by_actor
        """;
}
