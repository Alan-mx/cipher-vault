using System.Text;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Documents;

internal static class DocumentLinkIndexer
{
    private const int MaxIndexBytes = 256 * 1024;
    private const string RelationType = "references";

    public static async Task IndexSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid documentId,
        int revision,
        string title,
        string sourcePath,
        string format,
        string content,
        long timestamp,
        CancellationToken cancellationToken)
    {
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM document_links WHERE source_document_id = $id;";
            delete.Parameters.AddWithValue("$id", documentIdValue);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (format == "md")
        {
            var parsed = WikiLinkParser.Parse(content);
            EnsureIndexSize(parsed);
            foreach (var link in parsed)
            {
                var normalizedTarget = DocumentLinkKeys.NormalizeWikiTarget(
                    link.RawTarget);
                if (normalizedTarget.Length == 0)
                {
                    continue;
                }
                var resolution = await DocumentLinkResolver.ResolveAsync(
                    connection,
                    transaction,
                    normalizedTarget,
                    cancellationToken).ConfigureAwait(false);
                await InsertLinkAsync(
                    connection,
                    transaction,
                    documentIdValue,
                    revision,
                    resolution,
                    link,
                    normalizedTarget,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE documents
            SET links_indexed_revision = $revision
            WHERE document_id = $id;
            """;
        update.Parameters.AddWithValue("$revision", revision);
        update.Parameters.AddWithValue("$id", documentIdValue);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task RecordAliasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid documentId,
        string? oldTitle,
        string? oldSourcePath,
        string newTitle,
        string newSourcePath,
        long timestamp,
        CancellationToken cancellationToken)
    {
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        if (oldTitle is not null &&
            !string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
        {
            await InsertAliasAsync(
                connection,
                transaction,
                documentIdValue,
                "title",
                oldTitle,
                DocumentLinkKeys.NormalizeTitle(oldTitle),
                timestamp,
                cancellationToken).ConfigureAwait(false);
        }
        if (oldSourcePath is not null &&
            !string.Equals(oldSourcePath, newSourcePath, StringComparison.Ordinal))
        {
            await InsertAliasAsync(
                connection,
                transaction,
                documentIdValue,
                "path",
                oldSourcePath,
                DocumentLinkKeys.NormalizeDocumentPath(oldSourcePath),
                timestamp,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task ReconcileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> normalizedTargets,
        CancellationToken cancellationToken)
    {
        foreach (var normalizedTarget in normalizedTargets
                     .Where(value => value.Length > 0)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var resolution = await DocumentLinkResolver.ResolveAsync(
                connection,
                transaction,
                normalizedTarget,
                cancellationToken).ConfigureAwait(false);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE document_links
                SET target_document_id = $target,
                    resolution_status = $status
                WHERE raw_target_norm = $rawTarget;
                """;
            update.Parameters.AddWithValue(
                "$target",
                resolution.TargetDocumentId is null
                    ? DBNull.Value
                    : resolution.TargetDocumentId.Value
                        .ToString("D")
                        .ToLowerInvariant());
            update.Parameters.AddWithValue(
                "$status",
                StatusText(resolution.Status));
            update.Parameters.AddWithValue("$rawTarget", normalizedTarget);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static IReadOnlyList<string> ResolutionKeys(
        string title,
        string sourcePath)
    {
        return
        [
            DocumentLinkKeys.NormalizeTitle(title),
            DocumentLinkKeys.NormalizeDocumentPath(sourcePath)
        ];
    }

    private static async Task InsertLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceDocumentId,
        int sourceRevision,
        LinkResolution resolution,
        ParsedWikiLink link,
        string normalizedTarget,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO document_links(
                link_id, source_document_id, source_revision, target_document_id,
                raw_target, raw_target_norm, display_text,
                target_heading, target_heading_norm, relation_type,
                resolution_status, start_offset, end_offset)
            VALUES(
                $id, $sourceId, $sourceRevision, $targetId,
                $rawTarget, $rawTargetNorm, $displayText,
                $targetHeading, $targetHeadingNorm, $relationType,
                $status, $startOffset, $endOffset);
            """;
        command.Parameters.AddWithValue(
            "$id",
            Guid.NewGuid().ToString("D").ToLowerInvariant());
        command.Parameters.AddWithValue("$sourceId", sourceDocumentId);
        command.Parameters.AddWithValue("$sourceRevision", sourceRevision);
        command.Parameters.AddWithValue(
            "$targetId",
            resolution.TargetDocumentId is null
                ? DBNull.Value
                : resolution.TargetDocumentId.Value
                    .ToString("D")
                    .ToLowerInvariant());
        command.Parameters.AddWithValue("$rawTarget", link.RawTarget);
        command.Parameters.AddWithValue("$rawTargetNorm", normalizedTarget);
        command.Parameters.AddWithValue("$displayText", link.DisplayText);
        command.Parameters.AddWithValue(
            "$targetHeading",
            link.TargetHeading is null
                ? DBNull.Value
                : link.TargetHeading);
        command.Parameters.AddWithValue(
            "$targetHeadingNorm",
            link.TargetHeading is null
                ? DBNull.Value
                : DocumentLinkKeys.NormalizeHeading(link.TargetHeading));
        command.Parameters.AddWithValue("$relationType", RelationType);
        command.Parameters.AddWithValue("$status", StatusText(resolution.Status));
        command.Parameters.AddWithValue("$startOffset", link.StartOffset);
        command.Parameters.AddWithValue("$endOffset", link.EndOffset);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAliasAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        string kind,
        string value,
        string normalizedValue,
        long timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO document_link_aliases(
                document_id, alias_kind, alias_value, alias_norm, created_at_ms)
            VALUES($id, $kind, $value, $norm, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$norm", normalizedValue);
        command.Parameters.AddWithValue("$createdAt", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureIndexSize(IReadOnlyList<ParsedWikiLink> links)
    {
        var bytes = 0;
        foreach (var link in links)
        {
            bytes += 64;
            bytes += Encoding.UTF8.GetByteCount(link.RawTarget);
            bytes += Encoding.UTF8.GetByteCount(link.DisplayText);
            bytes += link.TargetHeading is null
                ? 0
                : Encoding.UTF8.GetByteCount(link.TargetHeading);
            if (bytes > MaxIndexBytes)
            {
                throw new VaultException(
                    VaultErrorCode.LinkIndexTooLarge,
                    "The document link index exceeds 256 KB.");
            }
        }
    }

    private static string StatusText(DocumentLinkResolutionStatus status)
    {
        return status switch
        {
            DocumentLinkResolutionStatus.Resolved => "resolved",
            DocumentLinkResolutionStatus.Ambiguous => "ambiguous",
            _ => "unresolved"
        };
    }
}
