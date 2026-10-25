using System.Text;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage;

internal static class DocumentLinkStore
{
    public static async Task<VaultDocumentMap> GetDocumentMapAsync(
        SqliteConnection connection,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        using var document = connection.CreateCommand();
        document.CommandText =
            """
            SELECT document_id, title, source_path, format, revision,
                   headings_json, content, updated_at_ms
            FROM documents
            WHERE document_id = $id AND deleted_at_ms IS NULL;
            """;
        document.Parameters.AddWithValue("$id", documentIdValue);

        string title;
        string sourcePath;
        string format;
        int revision;
        string headingsJson;
        string content;
        long updatedAt;
        await using (var reader = await document.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new VaultException(
                    VaultErrorCode.NotFound,
                    "Document was not found.");
            }
            title = reader.GetString(1);
            sourcePath = reader.GetString(2);
            format = reader.GetString(3);
            revision = reader.GetInt32(4);
            headingsJson = reader.GetString(5);
            content = reader.GetString(6);
            updatedAt = reader.GetInt64(7);
        }

        var outgoing = await ReadOutgoingLinksAsync(
            connection,
            documentIdValue,
            cancellationToken).ConfigureAwait(false);
        var frontMatter = FrontMatterDocument.TryParse(content, out var snapshot)
            ? snapshot.Fields
                .Select(field => new VaultFrontMatterField(
                    field.Key,
                    field.ValueType,
                    field.Value.GetRawText()))
                .ToArray()
            : [];

        using var backlinks = connection.CreateCommand();
        backlinks.CommandText =
            """
            SELECT count(*)
            FROM document_links
            JOIN documents source
              ON source.document_id = document_links.source_document_id
            WHERE document_links.target_document_id = $id
              AND document_links.resolution_status = 'resolved'
              AND source.deleted_at_ms IS NULL;
            """;
        backlinks.Parameters.AddWithValue("$id", documentIdValue);
        var backlinkCount = Convert.ToInt32(
            await backlinks.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        return new VaultDocumentMap(
            documentId,
            title,
            sourcePath,
            format,
            revision,
            MarkdownHeadings.ParseWithPaths(headingsJson),
            frontMatter,
            outgoing,
            backlinkCount,
            DateTimeOffset.FromUnixTimeMilliseconds(updatedAt));
    }

    public static async Task<VaultBacklinkPage> ListBacklinksAsync(
        SqliteConnection connection,
        Guid sessionId,
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 100);
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        await EnsureActiveDocumentAsync(
            connection,
            documentIdValue,
            cancellationToken).ConfigureAwait(false);
        var fingerprint = CursorCodec.Fingerprint(documentIdValue);
        BacklinkCursor? position = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            try
            {
                if (CursorCodec.TryDecode(
                        cursor,
                        sessionId,
                        "backlinks",
                        fingerprint,
                        out BacklinkCursor decoded))
                {
                    position = decoded;
                }
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.InvalidInput)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidCursor,
                    "The backlink cursor is invalid.",
                    exception);
            }
        }

        var sql = new StringBuilder(
            """
            SELECT source.document_id, source.title, source.source_path,
                   source.revision, count(*),
                   MIN(document_links.target_heading),
                   source.updated_at_ms
            FROM document_links
            JOIN documents source
              ON source.document_id = document_links.source_document_id
            WHERE document_links.target_document_id = $targetId
              AND document_links.resolution_status = 'resolved'
              AND source.deleted_at_ms IS NULL
            """);
        if (position is not null)
        {
            sql.Append(
                """
                 AND (
                    source.updated_at_ms < $afterUpdated
                    OR (
                        source.updated_at_ms = $afterUpdated
                        AND source.document_id > $afterDocumentId
                    )
                 )
                """);
        }
        sql.Append(
            """
             GROUP BY source.document_id, source.title, source.source_path,
                      source.revision, source.updated_at_ms
             ORDER BY source.updated_at_ms DESC, source.document_id ASC
             LIMIT $limit;
            """);

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$targetId", documentIdValue);
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        if (position is not null)
        {
            command.Parameters.AddWithValue("$afterUpdated", position.UpdatedAtMs);
            command.Parameters.AddWithValue(
                "$afterDocumentId",
                position.SourceDocumentId);
        }

        var rows = new List<(VaultBacklinkItem Item, BacklinkCursor Cursor)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = new VaultBacklinkItem(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
                rows.Add((
                    item,
                    new BacklinkCursor(
                        reader.GetInt64(6),
                        reader.GetString(0))));
            }
        }

        var hasMore = rows.Count > effectiveLimit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "backlinks",
                fingerprint,
                rows[^1].Cursor)
            : null;
        return new VaultBacklinkPage(
            rows.Select(row => row.Item).ToArray(),
            nextCursor);
    }

    public static async Task<VaultUnlinkedMentionPage>
        ListUnlinkedMentionsAsync(
            SqliteConnection connection,
            Guid sessionId,
            Guid documentId,
            string? cursor,
            int limit,
            CancellationToken cancellationToken)
    {
        const int maxCandidateScan = 500;
        const int maxOccurrencesPerDocument = 3;
        var effectiveLimit = Math.Clamp(limit, 1, 100);
        var targetId = documentId.ToString("D").ToLowerInvariant();
        var targetTitle = (await GetActiveDocumentTitleAsync(
                connection,
                targetId,
                cancellationToken)
            .ConfigureAwait(false))
            .Trim();
        var fingerprint = CursorCodec.Fingerprint(
            $"{targetId}\u001F{DocumentLinkKeys.NormalizeTitle(targetTitle)}");
        UnlinkedMentionCursor? position = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            try
            {
                if (CursorCodec.TryDecode(
                        cursor,
                        sessionId,
                        "unlinked-mentions",
                        fingerprint,
                        out UnlinkedMentionCursor decoded))
                {
                    position = decoded;
                }
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.InvalidInput)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidCursor,
                    "The unlinked mention cursor is invalid.",
                    exception);
            }
        }

        var sql = new StringBuilder(
            """
            SELECT document_id, title, source_path, revision, content,
                   updated_at_ms
            FROM documents
            WHERE deleted_at_ms IS NULL
              AND format = 'md'
              AND document_id <> $targetId
            """);
        var canPrefilter = !HasCasedNonAscii(targetTitle);
        if (canPrefilter)
        {
            sql.Append(" AND content LIKE $pattern ESCAPE '\\'");
        }
        if (position is not null)
        {
            sql.Append(
                """
                 AND (
                    updated_at_ms < $afterUpdated
                    OR (
                        updated_at_ms = $afterUpdated
                        AND document_id > $afterDocumentId
                    )
                 )
                """);
        }
        sql.Append(
            """
             ORDER BY updated_at_ms DESC, document_id ASC
             LIMIT $limit;
            """);

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$targetId", targetId);
        if (canPrefilter)
        {
            command.Parameters.AddWithValue(
                "$pattern",
                $"%{EscapeLikeLiteral(targetTitle)}%");
        }
        command.Parameters.AddWithValue("$limit", maxCandidateScan + 1);
        if (position is not null)
        {
            command.Parameters.AddWithValue(
                "$afterUpdated",
                position.UpdatedAtMs);
            command.Parameters.AddWithValue(
                "$afterDocumentId",
                position.SourceDocumentId);
        }

        var items = new List<(
            VaultUnlinkedMentionItem Item,
            UnlinkedMentionCursor Cursor)>();
        UnlinkedMentionCursor? lastScanned = null;
        string? nextCursor = null;
        var scanned = 0;
        var candidateLimitReached = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (scanned >= maxCandidateScan)
            {
                candidateLimitReached = true;
                break;
            }
            scanned++;
            var sourceDocumentId = reader.GetString(0);
            var updatedAtMs = reader.GetInt64(5);
            var currentCursor = new UnlinkedMentionCursor(
                updatedAtMs,
                sourceDocumentId);
            lastScanned = currentCursor;
            var scan = UnlinkedMentionScanner.Scan(
                reader.GetString(4),
                targetTitle,
                maxOccurrencesPerDocument);
            if (scan.OccurrenceCount == 0)
            {
                continue;
            }

            items.Add((
                new VaultUnlinkedMentionItem(
                    Guid.Parse(sourceDocumentId),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    scan.OccurrenceCount,
                    scan.Occurrences,
                    DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs)),
                currentCursor));
            if (items.Count <= effectiveLimit)
            {
                continue;
            }
            items.RemoveAt(items.Count - 1);
            nextCursor = CursorCodec.Encode(
                sessionId,
                "unlinked-mentions",
                fingerprint,
                items[^1].Cursor);
            break;
        }

        if (nextCursor is null && candidateLimitReached && lastScanned is not null)
        {
            nextCursor = CursorCodec.Encode(
                sessionId,
                "unlinked-mentions",
                fingerprint,
                lastScanned);
        }

        return new VaultUnlinkedMentionPage(
            documentId,
            targetTitle,
            items.Select(item => item.Item).ToArray(),
            nextCursor);
    }

    public static async Task<VaultTagPage> ListTagsAsync(
        SqliteConnection connection,
        Guid sessionId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 100);
        const string fingerprintSeed = "active-tags";
        var fingerprint = CursorCodec.Fingerprint(fingerprintSeed);
        TagCursor? position = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            try
            {
                if (CursorCodec.TryDecode(
                        cursor,
                        sessionId,
                        "tags",
                        fingerprint,
                        out TagCursor decoded))
                {
                    position = decoded;
                }
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.InvalidInput)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidCursor,
                    "The tag cursor is invalid.",
                    exception);
            }
        }

        var sql = new StringBuilder(
            """
            SELECT tags.name, count(DISTINCT document_tags.document_id)
            FROM tags
            JOIN document_tags ON document_tags.tag_id = tags.tag_id
            JOIN documents ON documents.document_id = document_tags.document_id
            WHERE documents.deleted_at_ms IS NULL
            """);
        if (position is not null)
        {
            sql.Append(" AND tags.normalized_name > $after ");
        }
        sql.Append(
            """
             GROUP BY tags.tag_id, tags.name, tags.normalized_name
             ORDER BY tags.normalized_name ASC
             LIMIT $limit;
            """);

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$limit", effectiveLimit + 1);
        if (position is not null)
        {
            command.Parameters.AddWithValue(
                "$after",
                position.NormalizedName);
        }

        var rows = new List<(VaultTagItem Item, TagCursor Cursor)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = new VaultTagItem(
                    reader.GetString(0),
                    reader.GetInt32(1));
                var normalized = item.Name.ToLowerInvariant();
                rows.Add((item, new TagCursor(normalized)));
            }
        }

        var hasMore = rows.Count > effectiveLimit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                sessionId,
                "tags",
                fingerprint,
                rows[^1].Cursor)
            : null;
        return new VaultTagPage(
            rows.Select(row => row.Item).ToArray(),
            nextCursor);
    }

    private static async Task<IReadOnlyList<VaultOutgoingLink>> ReadOutgoingLinksAsync(
        SqliteConnection connection,
        string sourceDocumentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT document_links.raw_target,
                   document_links.display_text,
                   document_links.target_heading,
                   document_links.relation_type,
                   document_links.resolution_status,
                   document_links.start_offset,
                   document_links.end_offset,
                   document_links.raw_target_norm,
                   target.document_id,
                   target.title,
                   target.source_path,
                   CASE
                       WHEN target.document_id IS NOT NULL
                        AND target.deleted_at_ms IS NULL
                       THEN 1
                       ELSE 0
                   END
            FROM document_links
            LEFT JOIN documents target
              ON target.document_id = document_links.target_document_id
            WHERE document_links.source_document_id = $sourceId
            ORDER BY document_links.start_offset, document_links.end_offset;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceDocumentId);
        var rows = new List<OutgoingLinkRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new OutgoingLinkRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetString(7),
                    reader.IsDBNull(8)
                        ? null
                        : new VaultLinkTarget(
                            Guid.Parse(reader.GetString(8)),
                            reader.GetString(9),
                            reader.GetString(10)),
                    reader.GetInt32(11) == 1));
            }
        }

        var pendingTargets = rows
            .Where(row =>
                !row.TargetIsActive ||
                !string.Equals(
                    row.ResolutionStatus,
                    "resolved",
                    StringComparison.Ordinal))
            .Select(row => row.RawTargetNorm)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var resolutions = await DocumentLinkResolver.ResolveManyAsync(
            connection,
            transaction: null,
            pendingTargets,
            cancellationToken).ConfigureAwait(false);

        var result = new List<VaultOutgoingLink>(rows.Count);
        foreach (var row in rows)
        {
            var status = row.ResolutionStatus;
            var target = row.Target;
            IReadOnlyList<VaultLinkTarget> candidates = [];
            if (!row.TargetIsActive)
            {
                target = null;
                status = "unresolved";
            }
            if (!string.Equals(status, "resolved", StringComparison.Ordinal))
            {
                var resolution = resolutions[row.RawTargetNorm];
                status = resolution.Status switch
                {
                    DocumentLinkResolutionStatus.Resolved => "resolved",
                    DocumentLinkResolutionStatus.Ambiguous => "ambiguous",
                    _ => "unresolved"
                };
                target = resolution.TargetDocumentId is null
                    ? null
                    : resolution.Candidates[0];
                candidates = resolution.Status == DocumentLinkResolutionStatus.Ambiguous
                    ? resolution.Candidates
                    : [];
            }
            result.Add(new VaultOutgoingLink(
                row.RawTarget,
                row.DisplayText,
                row.TargetHeading,
                row.RelationType,
                status,
                target,
                candidates,
                row.StartOffset,
                row.EndOffset));
        }
        return result;
    }

    private static async Task EnsureActiveDocumentAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM documents WHERE document_id = $id AND deleted_at_ms IS NULL;";
        command.Parameters.AddWithValue("$id", documentId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new VaultException(
                VaultErrorCode.NotFound,
                "Document was not found.");
        }
    }

    private static async Task<string> GetActiveDocumentTitleAsync(
        SqliteConnection connection,
        string documentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT title
            FROM documents
            WHERE document_id = $id AND deleted_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result is not string title)
        {
            throw new VaultException(
                VaultErrorCode.NotFound,
                "Document was not found.");
        }
        return title;
    }

    private static string EscapeLikeLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static bool HasCasedNonAscii(string value)
    {
        return value.Any(character =>
            character > 127 &&
            (char.IsUpper(character) || char.IsLower(character)));
    }

    private sealed record BacklinkCursor(long UpdatedAtMs, string SourceDocumentId);

    private sealed record UnlinkedMentionCursor(
        long UpdatedAtMs,
        string SourceDocumentId);

    private sealed record TagCursor(string NormalizedName);

    private sealed record OutgoingLinkRow(
        string RawTarget,
        string DisplayText,
        string? TargetHeading,
        string RelationType,
        string ResolutionStatus,
        int StartOffset,
        int EndOffset,
        string RawTargetNorm,
        VaultLinkTarget? Target,
        bool TargetIsActive);
}
