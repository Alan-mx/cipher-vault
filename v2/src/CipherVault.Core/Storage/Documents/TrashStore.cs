using System.Globalization;
using System.Text;
using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Relations;
using CipherVault.Core.Storage.Semantic;
using CipherVault.Core.Storage.Relations;
using CipherVault.Core.Storage.Tags;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Documents;

public static class TrashStore
{
    public static async Task<TrashPage> ListAsync(
        VaultOperationContext context,
        CursorPageRequest page,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pageSize = NormalizePageSize(page?.PageSize);
        var fingerprint = CursorCodec.Fingerprint("trash");
        var hasCursor = CursorCodec.TryDecode(
            page?.Cursor,
            "trash",
            fingerprint,
            out TrashCursor? cursor);
        if (page?.Cursor is not null && !hasCursor)
        {
            throw InvalidCursor();
        }
        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();
        var items = new List<(TrashItem Item, TrashCursor Cursor)>();
        using var read = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT document_id, title, path, revision, deleted_at, purge_eligible_at
            FROM documents
            WHERE deleted_at IS NOT NULL
              AND deleted_at <= $snapshotAt
            """);
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    deleted_at < $afterDeletedAt
                    OR (
                        deleted_at = $afterDeletedAt
                        AND document_id < $afterDocumentId
                    )
                 )
                """);
        }
        sql.Append(
            """
             ORDER BY deleted_at DESC, document_id DESC
             LIMIT $limit;
            """);
        read.Command.CommandText = sql.ToString();
        read.Command.Parameters.AddWithValue("$snapshotAt", snapshotAt.ToString("O"));
        read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        if (cursor is not null)
        {
            read.Command.Parameters.AddWithValue(
                "$afterDeletedAt",
                cursor.DeletedAt.ToString("O"));
            read.Command.Parameters.AddWithValue(
                "$afterDocumentId",
                cursor.DocumentId.ToString("D"));
        }
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var deletedAt = ParseTimestamp(reader.GetString(4));
            var id = Guid.Parse(reader.GetString(0));
            items.Add((
                new TrashItem(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    deletedAt,
                    ParseTimestamp(reader.GetString(5))),
                new TrashCursor(deletedAt, id, snapshotAt)));
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode("trash", fingerprint, items[^1].Cursor)
            : null;
        return new TrashPage(
            items.Select(item => item.Item).ToArray(),
            nextCursor);
    }

    public static async Task<DocumentWriteResult> RestoreAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        string? replacementPath,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireUser(actor);
        var current = await ReadTrashedAsync(context, documentId, cancellationToken);
        if (current.Revision != expectedRevision)
        {
            throw new VaultException(
                VaultErrorCode.RevisionConflict,
                "Document revision changed.",
                details: new Dictionary<string, object?>
                {
                    ["currentRevision"] = current.Revision
                });
        }

        var format = VaultDocumentFormatExtensions.ParseDatabaseValue(current.Format);
        var path = VirtualPath.Normalize(
            replacementPath ?? current.Path,
            format);
        var newRevision = current.Revision + 1;
        try
        {
            using (var update = context.CreateCommand())
            {
                update.Command.CommandText =
                    """
                    UPDATE documents
                    SET path = $path,
                        path_key = $pathKey,
                        revision = $revision,
                        updated_at = $updatedAt,
                        deleted_at = NULL,
                        purge_eligible_at = NULL
                    WHERE document_id = $documentId
                      AND revision = $expectedRevision
                      AND deleted_at IS NOT NULL;
                    """;
                update.Command.Parameters.AddWithValue("$path", path.Path);
                update.Command.Parameters.AddWithValue("$pathKey", path.PathKey);
                update.Command.Parameters.AddWithValue("$revision", newRevision);
                update.Command.Parameters.AddWithValue(
                    "$updatedAt",
                    now.ToUniversalTime().ToString("O"));
                update.Command.Parameters.AddWithValue(
                    "$documentId",
                    documentId.ToString("D"));
                update.Command.Parameters.AddWithValue(
                    "$expectedRevision",
                    expectedRevision);
                if (await update.Command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new VaultException(
                        VaultErrorCode.RevisionConflict,
                        "Document revision changed.");
                }
            }

            var tags = TagNormalizer.NormalizeMany(current.TagNames);
            var links = format == VaultDocumentFormat.Markdown
                ? DocumentLinkParser.Parse(current.Content, path)
                : [];
            await DocumentIndexWriter.ReplaceFtsAsync(
                context,
                documentId,
                current.Title,
                path.Path,
                current.HeadingsJson,
                tags,
                current.Content,
                cancellationToken);
            await DocumentIndexWriter.ReplaceLinksAsync(
                context,
                documentId,
                newRevision,
                path,
                links,
                cancellationToken);
            await DocumentIndexWriter.ReconcileIncomingLinksAsync(
                context,
                DocumentIndexWriter.ResolutionAliases(
                    current.Title,
                    path.Path,
                    current.Format),
                cancellationToken);
            _ = await DocumentRevisionWriter.InsertAsync(
                context,
                documentId,
                newRevision,
                current.Title,
                path.Path,
                current.Format,
                current.Content,
                current.FrontMatterJson,
                current.HeadingsJson,
                tags,
                now,
                cancellationToken);
            await SemanticIndexQueue.QueueIndexAsync(
                context,
                documentId,
                newRevision,
                current.Content,
                now,
                cancellationToken);
            await MarkRelationSuggestionsStaleAsync(
                context,
                documentId,
                newRevision,
                now,
                cancellationToken);
            var auditEventId = await AuditStore.AppendAsync(
                context,
                actor,
                "restore",
                documentId,
                newRevision,
                "success",
                new { restored = true },
                now,
                cancellationToken);
            return new DocumentWriteResult(
                documentId,
                newRevision,
                path.Path,
                now,
                auditEventId);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new VaultException(
                VaultErrorCode.DocumentPathConflict,
                "The restore path is already in use.");
        }
    }

    public static async Task<Guid?> PurgeAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireUser(actor);
        var current = await ReadTrashedAsync(context, documentId, cancellationToken);
        if (current.Revision != expectedRevision)
        {
            throw new VaultException(
                VaultErrorCode.RevisionConflict,
                "Document revision changed.",
                details: new Dictionary<string, object?>
                {
                    ["currentRevision"] = current.Revision
                });
        }
        if (now < current.PurgeEligibleAt)
        {
            throw new VaultException(
                VaultErrorCode.PurgeNotEligible,
                "Document is not eligible for permanent deletion yet.",
                details: new Dictionary<string, object?>
                {
                    ["purgeEligibleAt"] = current.PurgeEligibleAt
                });
        }

        await SemanticIndexQueue.QueuePurgeAsync(
            context,
            documentId,
            current.Revision,
            now,
            cancellationToken);
        var auditEventId = await AuditStore.AppendAsync(
            context,
            actor,
            "purge",
            documentId,
            current.Revision,
            "success",
            new { purged = true },
            now,
            cancellationToken);
        await DocumentIndexWriter.RemoveFtsAsync(
            context,
            documentId,
            cancellationToken);
        using (var delete = context.CreateCommand())
        {
            delete.Command.CommandText =
                "DELETE FROM documents WHERE document_id = $documentId;";
            delete.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await delete.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        await TagStore.CleanupOrphansAsync(context, cancellationToken);
        return auditEventId;
    }

    private static async Task<TrashedDocumentState> ReadTrashedAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        TrashedDocumentState state;
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                """
                SELECT title, path, format, content, frontmatter_json,
                       headings_json, revision, deleted_at, purge_eligible_at
                FROM documents
                WHERE document_id = $documentId AND deleted_at IS NOT NULL;
                """;
            read.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new VaultException(
                    VaultErrorCode.DocumentNotFound,
                    "Trashed document was not found.");
            }
            state = new TrashedDocumentState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                ParseTimestamp(reader.GetString(7)),
                ParseTimestamp(reader.GetString(8)),
                []);
        }

        var tags = new List<string>();
        using (var tagRead = context.CreateCommand())
        {
            tagRead.Command.CommandText =
                """
                SELECT t.name
                FROM document_tags AS dt
                JOIN tags AS t ON t.tag_id = dt.tag_id
                WHERE dt.document_id = $documentId
                ORDER BY t.normalized_key;
                """;
            tagRead.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader = await tagRead.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tags.Add(reader.GetString(0));
            }
        }
        return state with { TagNames = tags.ToArray() };
    }

    private static void RequireUser(VaultActorContext actor)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "This trash operation requires a user action.");
        }
    }

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 20;
        if (pageSize is < 1 or > 100)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Trash page size must be between 1 and 100.");
        }
        return pageSize;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static VaultException InvalidCursor() =>
        new(VaultErrorCode.InvalidCursor, "Cursor is invalid for this trash query.");

    private static async Task MarkRelationSuggestionsStaleAsync(
        VaultOperationContext context,
        Guid documentId,
        int documentRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var updates =
            await RelationSuggestionStore.MarkPendingStaleForDocumentAsync(
                context,
                documentId,
                trashed: false,
                now,
                cancellationToken);
        var actor = new VaultActorContext(VaultActorKind.System);
        foreach (var update in updates)
        {
            await AuditStore.AppendAsync(
                context,
                actor,
                "relation_suggestion.stale",
                documentId,
                documentRevision,
                "success",
                new
                {
                    suggestionId = update.SuggestionId,
                    suggestionRevision = update.SuggestionRevision,
                    relationType = update.RelationType.ToDatabaseValue(),
                    sourceDocumentId = update.SourceDocumentId,
                    targetDocumentId = update.TargetDocumentId,
                    status = "stale",
                    reasonCode = update.StaleReason.ToDatabaseValue(),
                    sessionGeneration = context.SessionGeneration
                },
                now,
                cancellationToken);
        }
    }

    private sealed record TrashCursor(
        DateTimeOffset DeletedAt,
        Guid DocumentId,
        DateTimeOffset SnapshotAt);

    private sealed record TrashedDocumentState(
        string Title,
        string Path,
        string Format,
        string Content,
        string FrontMatterJson,
        string HeadingsJson,
        int Revision,
        DateTimeOffset DeletedAt,
        DateTimeOffset PurgeEligibleAt,
        IReadOnlyList<string> TagNames);
}
