using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Tags;

namespace CipherVault.Core.Storage.Documents;

internal static class DocumentIndexWriter
{
    internal static async Task ReplaceAllAsync(
        VaultOperationContext context,
        Guid documentId,
        int revision,
        string title,
        NormalizedVirtualPath path,
        string headingsJson,
        string content,
        IReadOnlyList<NormalizedTag> tags,
        IReadOnlyList<ParsedDocumentLink> links,
        CancellationToken cancellationToken)
    {
        await TagStore.ReplaceAsync(context, documentId, tags, cancellationToken);
        await ReplaceFtsAsync(
            context,
            documentId,
            title,
            path.Path,
            headingsJson,
            tags,
            content,
            cancellationToken);
        await ReplaceLinksAsync(
            context,
            documentId,
            revision,
            path,
            links,
            cancellationToken);
    }

    internal static async Task ReplaceFtsAsync(
        VaultOperationContext context,
        Guid documentId,
        string title,
        string path,
        string headingsJson,
        IReadOnlyList<NormalizedTag> tags,
        string content,
        CancellationToken cancellationToken)
    {
        var documentIdText = documentId.ToString("D");
        using (var delete = context.CreateCommand())
        {
            delete.Command.CommandText =
                "DELETE FROM document_fts WHERE document_id = $documentId;";
            delete.Command.Parameters.AddWithValue("$documentId", documentIdText);
            await delete.Command.ExecuteNonQueryAsync(cancellationToken);
        }

        var headings = string.Join(
            '\n',
            MarkdownHeadings.Parse(headingsJson));
        var tagText = string.Join(
            '\n',
            tags.Select(tag => tag.Name));
        using var insert = context.CreateCommand();
        insert.Command.CommandText =
            """
            INSERT INTO document_fts(document_id, title, path, headings, tags, content)
            VALUES($documentId, $title, $path, $headings, $tags, $content);
            """;
        insert.Command.Parameters.AddWithValue("$documentId", documentIdText);
        insert.Command.Parameters.AddWithValue("$title", title);
        insert.Command.Parameters.AddWithValue("$path", path);
        insert.Command.Parameters.AddWithValue("$headings", headings);
        insert.Command.Parameters.AddWithValue("$tags", tagText);
        insert.Command.Parameters.AddWithValue("$content", content);
        await insert.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task RemoveFtsAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        using var delete = context.CreateCommand();
        delete.Command.CommandText =
            "DELETE FROM document_fts WHERE document_id = $documentId;";
        delete.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        await delete.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task ReplaceLinksAsync(
        VaultOperationContext context,
        Guid sourceDocumentId,
        int sourceRevision,
        NormalizedVirtualPath sourcePath,
        IReadOnlyList<ParsedDocumentLink> links,
        CancellationToken cancellationToken)
    {
        using (var delete = context.CreateCommand())
        {
            delete.Command.CommandText =
                "DELETE FROM document_links WHERE source_document_id = $source;";
            delete.Command.Parameters.AddWithValue(
                "$source",
                sourceDocumentId.ToString("D"));
            await delete.Command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var link in links)
        {
            await InsertLinkAsync(
                context,
                sourceDocumentId,
                sourceRevision,
                sourcePath,
                link,
                cancellationToken);
        }
    }

    internal static async Task ReconcileIncomingLinksAsync(
        VaultOperationContext context,
        DocumentResolutionAliases aliases,
        CancellationToken cancellationToken)
    {
        var rows = new List<ReconcileLinkRow>();
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                """
                 SELECT l.link_id, l.link_kind, l.raw_target,
                        l.raw_target_key, l.canonical_target_key
                 FROM document_links AS l
                 JOIN documents AS d ON d.document_id = l.source_document_id
                 WHERE d.deleted_at IS NULL
                   AND (
                       (
                           l.link_kind = 'wiki'
                           AND l.raw_target_key IN ($titleKey, $pathKey)
                       )
                       OR
                       (
                           l.link_kind = 'markdown'
                           AND l.canonical_target_key = $canonicalPathKey
                           AND (
                               l.resolution_status <> 'resolved'
                               OR l.target_document_id IS NULL
                           )
                       )
                   )
                 ORDER BY l.link_id;
                 """;
            read.Command.Parameters.AddWithValue("$titleKey", aliases.TitleKey);
            read.Command.Parameters.AddWithValue(
                "$pathKey",
                aliases.PathWithoutExtensionKey);
            read.Command.Parameters.AddWithValue(
                "$canonicalPathKey",
                aliases.PathKey);

            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ReconcileLinkRow(
                    Guid.Parse(reader.GetString(0)),
                    ParseKind(reader.GetString(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        foreach (var row in rows)
        {
            var parsed = new ParsedDocumentLink(
                row.Kind,
                row.RawTarget,
                row.RawTargetKey,
                row.CanonicalTargetKey,
                string.Empty,
                null,
                null,
                0,
                1,
                row.Kind == DocumentLinkKind.Markdown ? 1 : null,
                row.Kind == DocumentLinkKind.Markdown ? 2 : null);
            var resolution = await ResolveAsync(
                context,
                parsed,
                cancellationToken);
            using var update = context.CreateCommand();
            update.Command.CommandText =
                """
                UPDATE document_links
                SET target_document_id = $target,
                    resolution_status = $status,
                    candidate_document_ids_json = $candidates
                WHERE link_id = $linkId;
                """;
            update.Command.Parameters.AddWithValue(
                "$target",
                resolution.TargetDocumentId?.ToString("D") ?? (object)DBNull.Value);
            update.Command.Parameters.AddWithValue("$status", resolution.Status);
            update.Command.Parameters.AddWithValue(
                "$candidates",
                JsonSerializer.Serialize(
                    resolution.CandidateDocumentIds.Select(id => id.ToString("D"))));
            update.Command.Parameters.AddWithValue("$linkId", row.LinkId.ToString("D"));
            await update.Command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static DocumentResolutionAliases ResolutionAliases(
        string title,
        string path,
        string format)
    {
        var extension = format == "md" ? ".md" : ".txt";
        var pathWithoutExtension = path.EndsWith(
            extension,
            StringComparison.OrdinalIgnoreCase)
            ? path[..^extension.Length]
            : path;
        return new DocumentResolutionAliases(
            TitleNormalizer.NormalizeKey(title),
            pathWithoutExtension.ToUpperInvariant(),
            path);
    }

    private static async Task InsertLinkAsync(
        VaultOperationContext context,
        Guid sourceDocumentId,
        int sourceRevision,
        NormalizedVirtualPath sourcePath,
        ParsedDocumentLink link,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(
            context,
            link,
            cancellationToken);
        using var insert = context.CreateCommand();
        insert.Command.CommandText =
            """
            INSERT INTO document_links(
                link_id, source_document_id, source_revision, target_document_id,
                link_kind, raw_target, raw_target_key, canonical_target_key,
                display_text, target_heading, target_heading_key, relation_type,
                resolution_status, candidate_document_ids_json, start_offset,
                end_offset, destination_start_offset, destination_end_offset)
            VALUES(
                $linkId, $sourceId, $sourceRevision, $targetId,
                $linkKind, $rawTarget, $rawTargetKey, $canonicalTargetKey,
                $displayText, $targetHeading, $targetHeadingKey, 'references',
                $status, $candidates, $startOffset, $endOffset,
                $destinationStartOffset, $destinationEndOffset);
            """;
        insert.Command.Parameters.AddWithValue(
            "$linkId",
            Guid.NewGuid().ToString("D"));
        insert.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceDocumentId.ToString("D"));
        insert.Command.Parameters.AddWithValue("$sourceRevision", sourceRevision);
        insert.Command.Parameters.AddWithValue(
            "$targetId",
            resolution.TargetDocumentId?.ToString("D") ?? (object)DBNull.Value);
        insert.Command.Parameters.AddWithValue(
            "$linkKind",
            ToDatabaseValue(link.Kind));
        insert.Command.Parameters.AddWithValue("$rawTarget", link.RawTarget);
        insert.Command.Parameters.AddWithValue(
            "$rawTargetKey",
            link.RawTargetKey ?? (object)DBNull.Value);
        insert.Command.Parameters.AddWithValue(
            "$canonicalTargetKey",
            link.CanonicalTargetKey ?? (object)DBNull.Value);
        insert.Command.Parameters.AddWithValue("$displayText", link.DisplayText);
        insert.Command.Parameters.AddWithValue(
            "$targetHeading",
            link.TargetHeading ?? (object)DBNull.Value);
        insert.Command.Parameters.AddWithValue(
            "$targetHeadingKey",
            link.TargetHeadingKey ?? (object)DBNull.Value);
        insert.Command.Parameters.AddWithValue("$status", resolution.Status);
        insert.Command.Parameters.AddWithValue(
            "$candidates",
            JsonSerializer.Serialize(
                resolution.CandidateDocumentIds.Select(id => id.ToString("D"))));
        insert.Command.Parameters.AddWithValue("$startOffset", link.StartOffset);
        insert.Command.Parameters.AddWithValue("$endOffset", link.EndOffset);
        insert.Command.Parameters.AddWithValue(
            "$destinationStartOffset",
            link.DestinationStartOffset ?? (object)DBNull.Value);
        insert.Command.Parameters.AddWithValue(
            "$destinationEndOffset",
            link.DestinationEndOffset ?? (object)DBNull.Value);
        await insert.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LinkResolution> ResolveAsync(
        VaultOperationContext context,
        ParsedDocumentLink link,
        CancellationToken cancellationToken)
    {
        var candidates = link.Kind switch
        {
            DocumentLinkKind.Markdown => await ResolveMarkdownAsync(
                context,
                link.CanonicalTargetKey,
                cancellationToken),
            DocumentLinkKind.Wiki => await ResolveWikiAsync(
                context,
                link.RawTargetKey,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(link))
        };

        return candidates.Count switch
        {
            0 => LinkResolution.Unresolved,
            1 => new LinkResolution("resolved", candidates[0], []),
            _ => new LinkResolution("ambiguous", null, candidates)
        };
    }

    private static async Task<IReadOnlyList<Guid>> ResolveMarkdownAsync(
        VaultOperationContext context,
        string? canonicalTargetKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(canonicalTargetKey))
        {
            return [];
        }

        using var query = context.CreateCommand();
        query.Command.CommandText =
            """
            SELECT document_id
            FROM documents
            WHERE deleted_at IS NULL AND path_key = $pathKey
            ORDER BY document_id
            LIMIT 2;
            """;
        query.Command.Parameters.AddWithValue("$pathKey", canonicalTargetKey);
        return await ReadCandidateIdsAsync(query, cancellationToken);
    }

    private static async Task<IReadOnlyList<Guid>> ResolveWikiAsync(
        VaultOperationContext context,
        string? rawTargetKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rawTargetKey))
        {
            return [];
        }

        using var query = context.CreateCommand();
        query.Command.CommandText =
            """
            SELECT document_id
            FROM documents
            WHERE deleted_at IS NULL
              AND (
                  title_key = $key
                  OR upper(
                      CASE
                          WHEN substr(path_key, -3) = '.md'
                              THEN substr(path_key, 1, length(path_key) - 3)
                          WHEN substr(path_key, -4) = '.txt'
                              THEN substr(path_key, 1, length(path_key) - 4)
                          ELSE path_key
                      END
                  ) = $key
              )
            ORDER BY path_key, document_id;
            """;
        query.Command.Parameters.AddWithValue("$key", rawTargetKey);
        return await ReadCandidateIdsAsync(query, cancellationToken);
    }

    private static async Task<IReadOnlyList<Guid>> ReadCandidateIdsAsync(
        VaultCommandLease command,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>();
        await using var reader = await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(Guid.Parse(reader.GetString(0)));
        }
        return candidates;
    }

    private static string ToDatabaseValue(DocumentLinkKind kind) =>
        kind switch
        {
            DocumentLinkKind.Wiki => "wiki",
            DocumentLinkKind.Markdown => "markdown",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static DocumentLinkKind ParseKind(string value) =>
        value switch
        {
            "wiki" => DocumentLinkKind.Wiki,
            "markdown" => DocumentLinkKind.Markdown,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    internal sealed record DocumentResolutionAliases(
        string TitleKey,
        string PathWithoutExtensionKey,
        string PathKey);

    private sealed record LinkResolution(
        string Status,
        Guid? TargetDocumentId,
        IReadOnlyList<Guid> CandidateDocumentIds)
    {
        internal static LinkResolution Unresolved { get; } = new(
            "unresolved",
            null,
            []);
    }

    private sealed record ReconcileLinkRow(
        Guid LinkId,
        DocumentLinkKind Kind,
        string RawTarget,
        string? RawTargetKey,
        string? CanonicalTargetKey);
}
