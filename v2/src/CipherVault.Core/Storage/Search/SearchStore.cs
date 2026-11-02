using System.Globalization;
using System.Text;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;

namespace CipherVault.Core.Storage.Search;

public static class SearchStore
{
    private const int ShortQueryLimit = 10_000;
    private const int MaxSnippetLength = 2_000;

    public static async Task<SearchPage> SearchAsync(
        VaultOperationContext context,
        SearchQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalizedQuery = query.Query
            .Normalize(System.Text.NormalizationForm.FormC)
            .Trim();
        int scalarCount;
        try
        {
            scalarCount = UnicodeText.CountScalars(normalizedQuery);
        }
        catch (ArgumentException)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Search query contains invalid Unicode data.");
        }
        if (scalarCount is < 1 or > 200)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Search query must contain 1 to 200 Unicode characters.");
        }
        if (query.OrphanOnly && query.HasBacklinks == true)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "orphanOnly and hasBacklinks=true cannot be combined.");
        }

        var pageSize = NormalizePageSize(query.Page?.PageSize);
        var tags = TagNormalizer.NormalizeMany(query.Tags);
        var format = query.Format?.ToDatabaseValue();
        var filterFingerprint = CursorCodec.Fingerprint(
            normalizedQuery,
            string.Join('\u001F', tags.Select(tag => tag.NormalizedKey)),
            format,
            query.UpdatedAfter?.ToUniversalTime().ToString("O"),
            query.UpdatedBefore?.ToUniversalTime().ToString("O"),
            query.HasBacklinks?.ToString(),
            query.OrphanOnly.ToString());
        var fingerprint = CursorCodec.Fingerprint(
            "search",
            filterFingerprint);
        var hasCursor = CursorCodec.TryDecode(
            query.Page?.Cursor,
            "search",
            fingerprint,
            out SearchCursor? cursor);
        if (query.Page?.Cursor is not null && !hasCursor)
        {
            throw InvalidCursor();
        }
        var snapshotAt = cursor?.SnapshotAt ?? now.ToUniversalTime();
        var totalActive = await CountTotalActiveAsync(context, cancellationToken);

        return scalarCount >= 3
            ? await SearchFtsAsync(
                context,
                normalizedQuery,
                tags,
                format,
                query,
                pageSize,
                fingerprint,
                cursor,
                snapshotAt,
                totalActive,
                cancellationToken)
            : await SearchShortAsync(
                context,
                normalizedQuery,
                tags,
                format,
                query,
                pageSize,
                fingerprint,
                cursor,
                snapshotAt,
                totalActive,
                cancellationToken);
    }

    private static async Task<SearchPage> SearchFtsAsync(
        VaultOperationContext context,
        string query,
        IReadOnlyList<NormalizedTag> tags,
        string? format,
        SearchQuery request,
        int pageSize,
        string fingerprint,
        SearchCursor? cursor,
        DateTimeOffset snapshotAt,
        int totalActive,
        CancellationToken cancellationToken)
    {
        var totalMatches = await CountFtsMatchesAsync(
            context,
            query,
            tags,
            format,
            request,
            snapshotAt,
            cancellationToken);
        using var read = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT d.document_id, d.title, d.path, d.revision,
                   bm25(document_fts) AS rank,
                   snippet(document_fts, 5, '', '', ' … ', 32),
                   d.updated_at
            FROM document_fts
            JOIN documents AS d ON d.document_id = document_fts.document_id
            WHERE document_fts MATCH $match
              AND d.deleted_at IS NULL
              AND d.updated_at <= $snapshotAt
            """);
        var parameterIndex = 0;
        AppendCommonFilters(
            sql,
            read,
            request,
            tags,
            format,
            ref parameterIndex);
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    bm25(document_fts) > $afterScore
                    OR (
                        bm25(document_fts) = $afterScore
                        AND (
                            d.updated_at < $afterUpdated
                            OR (
                                d.updated_at = $afterUpdated
                                AND d.document_id > $afterDocumentId
                            )
                        )
                    )
                 )
                """);
            read.Command.Parameters.AddWithValue("$afterScore", cursor.Score);
            read.Command.Parameters.AddWithValue(
                "$afterUpdated",
                cursor.UpdatedAt.ToString("O"));
            read.Command.Parameters.AddWithValue(
                "$afterDocumentId",
                cursor.DocumentId.ToString("D"));
        }
        sql.Append(
            """
             ORDER BY rank ASC, d.updated_at DESC, d.document_id
             LIMIT $limit;
            """);
        read.Command.CommandText = sql.ToString();
        read.Command.Parameters.AddWithValue(
            "$match",
            FtsPhrase(query));
        read.Command.Parameters.AddWithValue("$snapshotAt", snapshotAt.ToString("O"));
        read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var hits = new List<(SearchHit Hit, SearchCursor Cursor)>();
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var score = reader.GetDouble(4);
            var updatedAt = ParseTimestamp(reader.GetString(6));
            var id = Guid.Parse(reader.GetString(0));
            hits.Add((
                new SearchHit(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    BoundSnippet(reader.IsDBNull(5) ? string.Empty : reader.GetString(5)),
                    score),
                new SearchCursor(score, updatedAt, id, snapshotAt)));
        }

        var hasMore = hits.Count > pageSize;
        if (hasMore)
        {
            hits.RemoveAt(hits.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode("search", fingerprint, hits[^1].Cursor)
            : null;
        var returned = hits.Select(hit => hit.Hit).ToArray();
        return new SearchPage(
            returned,
            new SearchCoverage(
                totalActive,
                totalActive,
                totalMatches,
                returned.Length,
                false),
            nextCursor);
    }

    private static async Task<int> CountFtsMatchesAsync(
        VaultOperationContext context,
        string query,
        IReadOnlyList<NormalizedTag> tags,
        string? format,
        SearchQuery request,
        DateTimeOffset snapshotAt,
        CancellationToken cancellationToken)
    {
        using var count = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT count(*)
            FROM document_fts
            JOIN documents AS d ON d.document_id = document_fts.document_id
            WHERE document_fts MATCH $match
              AND d.deleted_at IS NULL
              AND d.updated_at <= $snapshotAt
            """);
        var parameterIndex = 0;
        AppendCommonFilters(
            sql,
            count,
            request,
            tags,
            format,
            ref parameterIndex);
        count.Command.CommandText = sql.ToString();
        count.Command.Parameters.AddWithValue("$match", FtsPhrase(query));
        count.Command.Parameters.AddWithValue(
            "$snapshotAt",
            snapshotAt.ToString("O"));
        return Convert.ToInt32(
            await count.Command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<SearchPage> SearchShortAsync(
        VaultOperationContext context,
        string query,
        IReadOnlyList<NormalizedTag> tags,
        string? format,
        SearchQuery request,
        int pageSize,
        string fingerprint,
        SearchCursor? cursor,
        DateTimeOffset snapshotAt,
        int totalActive,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT d.document_id, d.title, d.path, d.revision, d.content, d.updated_at
            FROM documents AS d
            WHERE d.deleted_at IS NULL
              AND d.updated_at <= $snapshotAt
            """);
        var parameterIndex = 0;
        AppendCommonFilters(
            sql,
            read,
            request,
            tags,
            format,
            ref parameterIndex);
        sql.Append(" ORDER BY d.updated_at DESC, d.document_id LIMIT $shortLimit;");
        read.Command.CommandText = sql.ToString();
        read.Command.Parameters.AddWithValue("$snapshotAt", snapshotAt.ToString("O"));
        read.Command.Parameters.AddWithValue("$shortLimit", ShortQueryLimit + 1);

        var hits = new List<(SearchHit Hit, SearchCursor Cursor)>();
        var scanned = 0;
        var matched = 0;
        var truncated = false;
        var passedCursor = cursor is null;
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (scanned >= ShortQueryLimit)
            {
                truncated = true;
                break;
            }
            scanned++;
            var title = reader.GetString(1);
            var path = reader.GetString(2);
            var content = reader.GetString(4);
            if (!Contains(title, query)
                && !Contains(path, query)
                && !Contains(content, query))
            {
                continue;
            }
            matched++;
            var updatedAt = ParseTimestamp(reader.GetString(5));
            var id = Guid.Parse(reader.GetString(0));
            var itemCursor = new SearchCursor(1.0, updatedAt, id, snapshotAt);
            if (!passedCursor)
            {
                passedCursor = IsAfterCursor(itemCursor, cursor!);
                if (!passedCursor)
                {
                    continue;
                }
            }
            if (hits.Count < pageSize + 1)
            {
                hits.Add((
                    new SearchHit(
                        id,
                        title,
                        path,
                        reader.GetInt32(3),
                        BuildSnippet(content, query),
                        1.0),
                    itemCursor));
            }
        }

        var hasMore = hits.Count > pageSize;
        if (hasMore)
        {
            hits.RemoveAt(hits.Count - 1);
        }
        var nextCursor = hasMore
            ? CursorCodec.Encode("search", fingerprint, hits[^1].Cursor)
            : null;
        var returned = hits.Select(hit => hit.Hit).ToArray();
        return new SearchPage(
            returned,
            new SearchCoverage(
                totalActive,
                scanned,
                matched,
                returned.Length,
                truncated),
            nextCursor);
    }

    private static void AppendCommonFilters(
        StringBuilder sql,
        VaultCommandLease lease,
        SearchQuery query,
        IReadOnlyList<NormalizedTag> tags,
        string? format,
        ref int parameterIndex)
    {
        if (format is not null)
        {
            var name = $"$format{parameterIndex++}";
            sql.Append($" AND d.format = {name}");
            lease.Command.Parameters.AddWithValue(name, format);
        }
        if (query.UpdatedAfter is { } updatedAfter)
        {
            var name = $"$after{parameterIndex++}";
            sql.Append($" AND d.updated_at >= {name}");
            lease.Command.Parameters.AddWithValue(
                name,
                updatedAfter.ToUniversalTime().ToString("O"));
        }
        if (query.UpdatedBefore is { } updatedBefore)
        {
            var name = $"$before{parameterIndex++}";
            sql.Append($" AND d.updated_at <= {name}");
            lease.Command.Parameters.AddWithValue(
                name,
                updatedBefore.ToUniversalTime().ToString("O"));
        }
        if (tags.Count > 0)
        {
            var names = new List<string>();
            foreach (var tag in tags)
            {
                var name = $"$tag{parameterIndex++}";
                names.Add(name);
                lease.Command.Parameters.AddWithValue(name, tag.NormalizedKey);
            }
            sql.Append(
                $"""
                 AND (
                    SELECT count(DISTINCT t.normalized_key)
                    FROM document_tags AS dt
                    JOIN tags AS t ON t.tag_id = dt.tag_id
                    WHERE dt.document_id = d.document_id
                      AND t.normalized_key IN ({string.Join(", ", names)})
                 ) = {tags.Count}
                """);
        }
        if (query.HasBacklinks is { } hasBacklinks)
        {
            sql.Append(
                hasBacklinks
                    ? """
                       AND EXISTS (
                          SELECT 1
                          FROM document_links AS l
                          JOIN documents AS source
                            ON source.document_id = l.source_document_id
                          WHERE l.target_document_id = d.document_id
                            AND l.resolution_status = 'resolved'
                            AND source.deleted_at IS NULL
                            AND source.revision = l.source_revision
                       )
                      """
                    : """
                       AND NOT EXISTS (
                          SELECT 1
                          FROM document_links AS l
                          JOIN documents AS source
                            ON source.document_id = l.source_document_id
                          WHERE l.target_document_id = d.document_id
                            AND l.resolution_status = 'resolved'
                            AND source.deleted_at IS NULL
                            AND source.revision = l.source_revision
                       )
                      """);
        }
        if (query.OrphanOnly)
        {
            sql.Append(
                """
                 AND NOT EXISTS (
                    SELECT 1 FROM document_links AS l
                    JOIN documents AS source
                      ON source.document_id = l.source_document_id
                    WHERE l.target_document_id = d.document_id
                      AND l.resolution_status = 'resolved'
                      AND source.deleted_at IS NULL
                      AND source.revision = l.source_revision
                 )
                 AND NOT EXISTS (
                    SELECT 1 FROM document_links AS l
                    JOIN documents AS target
                      ON target.document_id = l.target_document_id
                    WHERE l.source_document_id = d.document_id
                      AND l.source_revision = d.revision
                      AND l.resolution_status = 'resolved'
                      AND target.deleted_at IS NULL
                 )
                """);
        }
    }

    private static async Task<int> CountTotalActiveAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        read.Command.CommandText =
            "SELECT count(*) FROM documents WHERE deleted_at IS NULL;";
        return Convert.ToInt32(await read.Command.ExecuteScalarAsync(cancellationToken));
    }

    private static bool IsAfterCursor(SearchCursor item, SearchCursor cursor)
    {
        if (item.Score != cursor.Score)
        {
            return item.Score > cursor.Score;
        }
        if (item.UpdatedAt != cursor.UpdatedAt)
        {
            return item.UpdatedAt < cursor.UpdatedAt;
        }
        return item.DocumentId.CompareTo(cursor.DocumentId) > 0;
    }

    private static bool Contains(string value, string query) =>
        value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string FtsPhrase(string query) =>
        $"\"{query.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string BuildSnippet(string content, string query)
    {
        var index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return BoundSnippet(content);
        }
        var start = Math.Max(0, index - 400);
        var end = Math.Min(content.Length, index + query.Length + 800);
        return BoundSnippet(content[start..end]);
    }

    private static string BoundSnippet(string value) =>
        value.Length <= MaxSnippetLength
            ? value
            : value[..MaxSnippetLength];

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 20;
        if (pageSize is < 1 or > 100)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Search page size must be between 1 and 100.");
        }
        return pageSize;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static VaultException InvalidCursor() =>
        new(VaultErrorCode.InvalidCursor, "Cursor is invalid for this search query.");

    private sealed record SearchCursor(
        double Score,
        DateTimeOffset UpdatedAt,
        Guid DocumentId,
        DateTimeOffset SnapshotAt);
}
