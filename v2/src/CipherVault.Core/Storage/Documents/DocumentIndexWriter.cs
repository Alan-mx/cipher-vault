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
        string path,
        string headingsJson,
        string content,
        IReadOnlyList<NormalizedTag> tags,
        IReadOnlyList<ParsedWikiLink> wikiLinks,
        CancellationToken cancellationToken)
    {
        await TagStore.ReplaceAsync(context, documentId, tags, cancellationToken);
        await ReplaceFtsAsync(
            context,
            documentId,
            title,
            path,
            headingsJson,
            tags,
            content,
            cancellationToken);
        await ReplaceLinksAsync(
            context,
            documentId,
            revision,
            wikiLinks,
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
        IReadOnlyList<ParsedWikiLink> wikiLinks,
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

        foreach (var link in wikiLinks)
        {
            await InsertLinkAsync(
                context,
                sourceDocumentId,
                sourceRevision,
                link,
                cancellationToken);
        }
    }

    internal static async Task ReconcileIncomingLinksAsync(
        VaultOperationContext context,
        IEnumerable<string> aliases,
        CancellationToken cancellationToken)
    {
        var rawTargetKeys = aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (rawTargetKeys.Length == 0)
        {
            return;
        }

        var placeholders = string.Join(
            ", ",
            rawTargetKeys.Select((_, index) => $"$key{index}"));
        var rows = new List<(Guid LinkId, Guid SourceId, int SourceRevision, string RawTarget)>();
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                $"""
                 SELECT l.link_id, l.source_document_id, l.source_revision, l.raw_target
                 FROM document_links AS l
                 JOIN documents AS d ON d.document_id = l.source_document_id
                 WHERE d.deleted_at IS NULL
                   AND l.raw_target_key IN ({placeholders})
                 ORDER BY l.link_id;
                 """;
            for (var index = 0; index < rawTargetKeys.Length; index++)
            {
                read.Command.Parameters.AddWithValue($"$key{index}", rawTargetKeys[index]);
            }

            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetString(3)));
            }
        }

        foreach (var row in rows)
        {
            var parsed = WikiLinkParser.Parse($"[[{row.RawTarget}]]");
            if (parsed.Count != 1)
            {
                continue;
            }

            var resolution = await ResolveAsync(
                context,
                parsed[0].RawTarget,
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

    internal static IReadOnlyList<string> ResolutionAliases(
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
        return
        [
            TitleNormalizer.NormalizeKey(title),
            pathWithoutExtension.ToUpperInvariant()
        ];
    }

    private static async Task InsertLinkAsync(
        VaultOperationContext context,
        Guid sourceDocumentId,
        int sourceRevision,
        ParsedWikiLink link,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(
            context,
            link.RawTarget,
            cancellationToken);
        using var insert = context.CreateCommand();
        insert.Command.CommandText =
            """
            INSERT INTO document_links(
                link_id, source_document_id, source_revision, target_document_id,
                raw_target, raw_target_key, display_text, target_heading,
                target_heading_key, relation_type, resolution_status,
                candidate_document_ids_json, start_offset, end_offset)
            VALUES(
                $linkId, $sourceId, $sourceRevision, $targetId,
                $rawTarget, $rawTargetKey, $displayText, $targetHeading,
                $targetHeadingKey, 'references', $status,
                $candidates, $startOffset, $endOffset);
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
        insert.Command.Parameters.AddWithValue("$rawTarget", link.RawTarget);
        insert.Command.Parameters.AddWithValue(
            "$rawTargetKey",
            NormalizeRawTargetKey(link.RawTarget));
        insert.Command.Parameters.AddWithValue("$displayText", link.DisplayText);
        insert.Command.Parameters.AddWithValue(
            "$targetHeading",
            link.TargetHeading ?? (object)DBNull.Value);
        insert.Command.Parameters.AddWithValue(
            "$targetHeadingKey",
            link.TargetHeading is null
                ? (object)DBNull.Value
                : UnicodeText.CollapseWhitespace(link.TargetHeading).ToUpperInvariant());
        insert.Command.Parameters.AddWithValue("$status", resolution.Status);
        insert.Command.Parameters.AddWithValue(
            "$candidates",
            JsonSerializer.Serialize(
                resolution.CandidateDocumentIds.Select(id => id.ToString("D"))));
        insert.Command.Parameters.AddWithValue("$startOffset", link.StartOffset);
        insert.Command.Parameters.AddWithValue("$endOffset", link.EndOffset);
        await insert.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LinkResolution> ResolveAsync(
        VaultOperationContext context,
        string rawTarget,
        CancellationToken cancellationToken)
    {
        var trimmed = rawTarget.Normalize().Trim().Trim('/');
        if (trimmed.Length == 0 || trimmed.Contains('\\'))
        {
            return LinkResolution.Unresolved;
        }

        var titleKey = NormalizeRawTargetKey(trimmed);
        var pathCandidates = BuildPathCandidates(trimmed);
        var placeholders = string.Join(
            ", ",
            pathCandidates.Select((_, index) => $"$path{index}"));
        using var query = context.CreateCommand();
        query.Command.CommandText =
            $"""
             SELECT document_id
             FROM documents
             WHERE deleted_at IS NULL
               AND (title_key = $titleKey OR path_key IN ({placeholders}))
             ORDER BY path_key, document_id;
             """;
        query.Command.Parameters.AddWithValue("$titleKey", titleKey);
        for (var index = 0; index < pathCandidates.Length; index++)
        {
            query.Command.Parameters.AddWithValue($"$path{index}", pathCandidates[index]);
        }

        var candidates = new List<Guid>();
        await using var reader = await query.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(Guid.Parse(reader.GetString(0)));
        }

        return candidates.Count switch
        {
            0 => LinkResolution.Unresolved,
            1 => new LinkResolution("resolved", candidates[0], []),
            _ => new LinkResolution("ambiguous", null, candidates)
        };
    }

    private static string[] BuildPathCandidates(string rawTarget)
    {
        var normalized = rawTarget.Normalize(System.Text.NormalizationForm.FormC);
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return [normalized];
        }

        return [normalized + ".md", normalized + ".txt"];
    }

    private static string NormalizeRawTargetKey(string rawTarget)
    {
        var normalized = rawTarget
            .Normalize(System.Text.NormalizationForm.FormC)
            .Trim()
            .Trim('/');
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return UnicodeText.CollapseWhitespace(normalized).ToUpperInvariant();
    }

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
}
