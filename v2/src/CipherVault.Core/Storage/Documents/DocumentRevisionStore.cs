using System.Globalization;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Storage.Documents;

public static class DocumentRevisionStore
{
    public static async Task<RevisionPage> ListAsync(
        VaultOperationContext context,
        Guid documentId,
        CursorPageRequest page,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = await DocumentQueryStore.GetMetadataAsync(context, documentId, cancellationToken);
        var pageSize = NormalizePageSize(page?.PageSize);
        var fingerprint = CursorCodec.Fingerprint(
            "revision",
            documentId.ToString("D"));
        var hasCursor = CursorCodec.TryDecode(
            page?.Cursor,
            "revision",
            fingerprint,
            out RevisionCursor? cursor);
        if (page?.Cursor is not null && !hasCursor)
        {
            throw InvalidCursor();
        }
        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();

        var items = new List<(RevisionItem Item, RevisionCursor Cursor)>();
        using var read = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT revision_id, revision, title, path, created_at, retain_until
            FROM document_revisions
            WHERE document_id = $documentId
              AND created_at <= $snapshotAt
            """);
        if (cursor is not null)
        {
            sql.Append(" AND revision < $afterRevision");
        }
        sql.Append(" ORDER BY revision DESC LIMIT $limit;");
        read.Command.CommandText = sql.ToString();
        read.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        read.Command.Parameters.AddWithValue("$snapshotAt", snapshotAt.ToString("O"));
        read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        if (cursor is not null)
        {
            read.Command.Parameters.AddWithValue("$afterRevision", cursor.Revision);
        }
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var revision = reader.GetInt32(1);
            items.Add((
                new RevisionItem(
                    Guid.Parse(reader.GetString(0)),
                    documentId,
                    revision,
                    reader.GetString(2),
                    reader.GetString(3),
                    ParseTimestamp(reader.GetString(4)),
                    ParseTimestamp(reader.GetString(5))),
                new RevisionCursor(revision, snapshotAt)));
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode("revision", fingerprint, items[^1].Cursor)
            : null;
        return new RevisionPage(
            items.Select(item => item.Item).ToArray(),
            nextCursor);
    }

    public static async Task<DocumentWriteResult> RestoreAsync(
        VaultOperationContext context,
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Revision restore requires a user action.");
        }

        var revision = await ReadRevisionAsync(
            context,
            documentId,
            revisionId,
            cancellationToken);
        return await DocumentMutationWriter.UpdateCoreAsync(
            context,
            documentId,
            expectedRevision,
            new UpdateDocumentRequest(
                revision.Title,
                revision.Path,
                revision.Content,
                revision.Tags),
            actor,
            now,
            "revision_restore",
            DocumentContentLimitMode.StandardWrite,
            cancellationToken);
    }

    public static async Task<RevisionContent> GetContentAsync(
        VaultOperationContext context,
        Guid documentId,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        _ = await DocumentQueryStore.GetMetadataAsync(
            context,
            documentId,
            cancellationToken);
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT revision_id, revision, title, path, format, tags_json,
                   content, created_at, retain_until
            FROM document_revisions
            WHERE document_id = $documentId AND revision_id = $revisionId;
            """;
        read.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        read.Command.Parameters.AddWithValue(
            "$revisionId",
            revisionId.ToString("D"));
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.DocumentNotFound,
                "Document revision was not found.");
        }

        var tags = JsonSerializer.Deserialize<string[]>(
            reader.GetString(5)) ?? [];
        return new RevisionContent(
            documentId,
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            VaultDocumentFormatExtensions.ParseDatabaseValue(
                reader.GetString(4)),
            tags,
            reader.GetString(6),
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)));
    }

    public static async Task<RevisionCleanupResult> CleanupExpiredAsync(
        VaultOperationContext context,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (actor.Kind != VaultActorKind.System)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Revision cleanup requires the system actor.");
        }

        int deleted;
        using (var delete = context.CreateCommand())
        {
            delete.Command.CommandText =
                "DELETE FROM document_revisions WHERE retain_until <= $now;";
            delete.Command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            deleted = await delete.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        var auditId = await AuditStore.AppendAsync(
            context,
            actor,
            "revision_cleanup",
            null,
            null,
            "success",
            new { deletedSnapshots = deleted },
            now,
            cancellationToken);
        return new RevisionCleanupResult(deleted, auditId);
    }

    private static async Task<RevisionSnapshot> ReadRevisionAsync(
        VaultOperationContext context,
        Guid documentId,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT title, path, content, tags_json
            FROM document_revisions
            WHERE document_id = $documentId AND revision_id = $revisionId;
            """;
        read.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        read.Command.Parameters.AddWithValue("$revisionId", revisionId.ToString("D"));
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.DocumentNotFound,
                "Document revision was not found.");
        }
        var tags = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
        return new RevisionSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            tags);
    }

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 20;
        if (pageSize is < 1 or > 100)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Revision page size must be between 1 and 100.");
        }
        return pageSize;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static VaultException InvalidCursor() =>
        new(VaultErrorCode.InvalidCursor, "Cursor is invalid for this revision query.");

    private sealed record RevisionCursor(int Revision, DateTimeOffset SnapshotAt);

    private sealed record RevisionSnapshot(
        string Title,
        string Path,
        string Content,
        IReadOnlyList<string> Tags);
}
