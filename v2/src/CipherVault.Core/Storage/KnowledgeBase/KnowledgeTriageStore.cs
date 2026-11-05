using System.Globalization;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Core.Storage.KnowledgeBase;

public static class KnowledgeTriageStore
{
    private const string CursorKind = "knowledge-triage";

    public static async Task<KnowledgeTriagePage> QueryAsync(
        VaultOperationContext context,
        KnowledgeModelSnapshot model,
        KnowledgeTriageQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (query.PageSize is < 1 or > 50)
        {
            throw new VaultException(
                VaultErrorCode.InvalidPageSize,
                "Triage pageSize must be between 1 and 50.");
        }
        if (query.StaleDays is not (7 or 30 or 90))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "staleDays must be 7, 30, or 90.");
        }
        var search = NormalizeSearch(query.Search);
        if (search.EnumerateRunes().Count() > 128)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Triage search must not exceed 128 characters.");
        }
        var reasonValue = query.Reason?.ToString() ?? string.Empty;
        var fingerprint = CursorCodec.Fingerprint(
            reasonValue,
            query.KnowledgeTypeId?.ToString("D"),
            query.StaleDays.ToString(CultureInfo.InvariantCulture),
            search,
            query.PageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Cursor,
            CursorKind,
            fingerprint,
            out TriageCursor? cursor);
        if (query.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Triage cursor is invalid for this query.");
        }
        var staleAsOf = cursor is null
            ? now.ToUniversalTime()
            : ParseTimestamp(cursor.StaleAsOf);
        var staleCutoff = staleAsOf.AddDays(-query.StaleDays);
        var useFts = search.EnumerateRunes().Count() >= 3;
        var baseCte = BuildBaseCte(
            search,
            useFts,
            query.KnowledgeTypeId is not null);

        var counts = await ReadCountsAsync(
            context,
            baseCte,
            query,
            search,
            staleCutoff,
            cancellationToken);
        var draftCounts = await ReadDraftCountsAsync(
            context,
            cancellationToken);
        var rows = await ReadRowsAsync(
            context,
            baseCte,
            query,
            search,
            staleCutoff,
            cursor,
            cancellationToken);
        var hasMore = rows.Count > query.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var tags = await ReadTagsAsync(
            context,
            rows.Select(item => item.KnowledgeId).ToArray(),
            cancellationToken);
        var items = rows.Select(row => ToItem(
                model,
                row,
                tags.GetValueOrDefault(row.KnowledgeId, [])))
            .ToArray();
        var nextCursor = hasMore && rows.Count > 0
            ? CursorCodec.Encode(
                CursorKind,
                fingerprint,
                new TriageCursor(
                    rows[^1].Priority,
                    rows[^1].UpdatedAtText,
                    rows[^1].KnowledgeId.ToString("D"),
                    staleAsOf.ToString("O")))
            : null;
        return new KnowledgeTriagePage(
            items,
            nextCursor,
            hasMore,
            counts with
            {
                Draft = draftCounts.Draft,
                Conflict = draftCounts.Conflict
            });
    }

    private static string BuildBaseCte(
        string search,
        bool useFts,
        bool filterType)
    {
        var sql = new StringBuilder(
            """
            WITH
            missing_required AS (
                SELECT k.knowledge_id
                FROM knowledge_items AS k
                JOIN knowledge_revisions AS r
                  ON r.knowledge_revision_id = k.current_revision_id
                JOIN knowledge_property_definitions AS p
                  ON p.knowledge_type_id = k.knowledge_type_id
                 AND p.is_enabled = 1
                 AND p.is_required = 1
                WHERE k.status = 'active'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM json_each(r.properties_json) AS j
                      WHERE j.key = p.property_key
                        AND j.type <> 'null'
                  )
                GROUP BY k.knowledge_id
            ),
            source_counts AS (
                SELECT r.knowledge_id,
                       count(sr.source_reference_id) AS source_count
                FROM knowledge_items AS k
                JOIN knowledge_revisions AS r
                  ON r.knowledge_revision_id = k.current_revision_id
                LEFT JOIN source_references AS sr
                  ON sr.knowledge_revision_id = r.knowledge_revision_id
                WHERE k.status = 'active'
                GROUP BY r.knowledge_id
            )
            """);
        if (useFts)
        {
            sql.Append(
                """
                , search_matches AS (
                    SELECT knowledge_id
                    FROM knowledge_fts
                    WHERE knowledge_fts MATCH $ftsSearch
                )
                """);
        }
        sql.Append(
            """
            , base AS (
                SELECT
                    k.knowledge_id,
                    k.title,
                    k.knowledge_type_id,
                    t.type_key,
                    t.name AS type_name,
                    k.updated_at,
                    r.revision,
                    r.properties_json,
                    CASE WHEN mr.knowledge_id IS NOT NULL THEN 1 ELSE 0 END
                      AS missing_required,
                    CASE WHEN coalesce(sc.source_count, 0) = 0 THEN 1 ELSE 0 END
                      AS missing_source,
                    CASE WHEN k.updated_at < $staleCutoff THEN 1 ELSE 0 END
                      AS stale,
                    coalesce(sc.source_count, 0) AS source_reference_count
                FROM knowledge_items AS k
                JOIN knowledge_types AS t ON t.type_id = k.knowledge_type_id
                JOIN knowledge_revisions AS r
                  ON r.knowledge_revision_id = k.current_revision_id
                LEFT JOIN missing_required AS mr
                  ON mr.knowledge_id = k.knowledge_id
                LEFT JOIN source_counts AS sc
                  ON sc.knowledge_id = k.knowledge_id
            """);
        if (useFts)
        {
            sql.Append('\n');
            sql.Append(
                """
                JOIN search_matches AS sm
                  ON sm.knowledge_id = k.knowledge_id
                """);
        }
        sql.Append(" WHERE k.status = 'active'");
        if (filterType)
        {
            sql.Append(" AND k.knowledge_type_id = $knowledgeTypeId");
        }
        if (!useFts && search.Length > 0)
        {
            sql.Append(
                """
                 AND (
                    k.title_key LIKE $titlePrefix ESCAPE '\'
                    OR EXISTS (
                        SELECT 1
                        FROM knowledge_tags AS kt
                        WHERE kt.knowledge_id = k.knowledge_id
                          AND kt.tag_key = $tagSearch
                    )
                 )
                """);
        }
        sql.Append(')');
        return sql.ToString();
    }

    private static async Task<KnowledgeTriageCounts> ReadCountsAsync(
        VaultOperationContext context,
        string baseCte,
        KnowledgeTriageQuery query,
        string search,
        DateTimeOffset staleCutoff,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {baseCte}
             SELECT
                coalesce(sum(missing_source), 0),
                coalesce(sum(missing_required), 0),
                coalesce(sum(stale), 0),
                coalesce(sum(
                    CASE
                        WHEN missing_required = 1
                          OR missing_source = 1
                          OR stale = 1
                        THEN 1 ELSE 0
                    END
                ), 0)
             FROM base;
             """;
        AddBaseParameters(
            command.Command,
            query,
            search,
            staleCutoff);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new KnowledgeTriageCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            0,
            0);
    }

    private static async Task<List<TriageRow>> ReadRowsAsync(
        VaultOperationContext context,
        string baseCte,
        KnowledgeTriageQuery query,
        string search,
        DateTimeOffset staleCutoff,
        TriageCursor? cursor,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        var sql = new StringBuilder(
            $"""
             {baseCte}
             ,
             ranked AS (
                 SELECT
                    base.*,
                    CASE
                        WHEN missing_required = 1 THEN 0
                        WHEN missing_source = 1 THEN 1
                        ELSE 2
                    END AS priority
                 FROM base
                 WHERE (
                    missing_required = 1
                    OR missing_source = 1
                    OR stale = 1
                 )
             """);
        if (query.Reason is { } reason)
        {
            sql.Append(
                reason switch
                {
                    KnowledgeTriageReason.MissingRequiredProperty =>
                        " AND missing_required = 1",
                    KnowledgeTriageReason.MissingSource =>
                        " AND missing_source = 1",
                    KnowledgeTriageReason.Stale =>
                        " AND stale = 1",
                    _ => string.Empty
                });
        }
        sql.Append(')');
        sql.Append(
            """
             SELECT
                knowledge_id,
                title,
                knowledge_type_id,
                type_key,
                type_name,
                updated_at,
                revision,
                properties_json,
                missing_required,
                missing_source,
                stale,
                source_reference_count,
                priority
             FROM ranked
            """);
        if (cursor is not null)
        {
            sql.Append(
                """
                 WHERE (
                    priority > $afterPriority
                    OR (
                        priority = $afterPriority
                        AND (
                            updated_at > $afterUpdatedAt
                            OR (
                                updated_at = $afterUpdatedAt
                                AND knowledge_id > $afterId
                            )
                        )
                    )
                 )
                """);
        }
        sql.Append(
            """
             ORDER BY priority, updated_at, knowledge_id
             LIMIT $limit;
            """);
        command.Command.CommandText = sql.ToString();
        AddBaseParameters(
            command.Command,
            query,
            search,
            staleCutoff);
        if (cursor is not null)
        {
            command.Command.Parameters.AddWithValue(
                "$afterPriority",
                cursor.Priority);
            command.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor.UpdatedAt);
            command.Command.Parameters.AddWithValue(
                "$afterId",
                cursor.KnowledgeId);
        }
        command.Command.Parameters.AddWithValue(
            "$limit",
            query.PageSize + 1);
        var rows = new List<TriageRow>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TriageRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt32(8) == 1,
                reader.GetInt32(9) == 1,
                reader.GetInt32(10) == 1,
                reader.GetInt32(11),
                reader.GetInt32(12)));
        }
        return rows;
    }

    private static void AddBaseParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        KnowledgeTriageQuery query,
        string search,
        DateTimeOffset staleCutoff)
    {
        command.Parameters.AddWithValue(
            "$staleCutoff",
            staleCutoff.ToUniversalTime().ToString("O"));
        if (query.KnowledgeTypeId is { } typeId)
        {
            command.Parameters.AddWithValue(
                "$knowledgeTypeId",
                typeId.ToString("D"));
        }
        if (search.EnumerateRunes().Count() >= 3)
        {
            command.Parameters.AddWithValue(
                "$ftsSearch",
                $"\"{search.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        }
        else if (search.Length > 0)
        {
            command.Parameters.AddWithValue(
                "$titlePrefix",
                EscapeLike(search.ToUpperInvariant()) + "%");
            command.Parameters.AddWithValue(
                "$tagSearch",
                TagNormalizer.Normalize(search).NormalizedKey);
        }
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> ReadTagsAsync(
        VaultOperationContext context,
        IReadOnlyCollection<Guid> knowledgeIds,
        CancellationToken cancellationToken)
    {
        if (knowledgeIds.Count == 0)
        {
            return [];
        }
        using var command = context.CreateCommand();
        var parameters = knowledgeIds.Select((_, index) => $"$id{index}")
            .ToArray();
        command.Command.CommandText =
            $"""
             SELECT knowledge_id, tag_label
             FROM knowledge_tags
             WHERE knowledge_id IN ({string.Join(',', parameters)})
             ORDER BY knowledge_id, tag_key;
             """;
        var index = 0;
        foreach (var id in knowledgeIds)
        {
            command.Command.Parameters.AddWithValue(
                parameters[index++],
                id.ToString("D"));
        }
        var result = new Dictionary<Guid, List<string>>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!result.TryGetValue(id, out var values))
            {
                values = [];
                result[id] = values;
            }
            values.Add(reader.GetString(1));
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value);
    }

    private static async Task<(int Draft, int Conflict)> ReadDraftCountsAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                coalesce(sum(CASE WHEN status = 'draft' THEN 1 ELSE 0 END), 0),
                coalesce(sum(CASE WHEN status = 'conflict' THEN 1 ELSE 0 END), 0)
            FROM drafts;
            """;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static KnowledgeTriageItem ToItem(
        KnowledgeModelSnapshot model,
        TriageRow row,
        IReadOnlyList<string> tags)
    {
        var reasons = new List<KnowledgeTriageReason>();
        if (row.MissingRequired)
        {
            reasons.Add(KnowledgeTriageReason.MissingRequiredProperty);
        }
        if (row.MissingSource)
        {
            reasons.Add(KnowledgeTriageReason.MissingSource);
        }
        if (row.Stale)
        {
            reasons.Add(KnowledgeTriageReason.Stale);
        }
        return new KnowledgeTriageItem(
            row.KnowledgeId,
            row.Title,
            row.KnowledgeTypeId,
            row.TypeKey,
            row.TypeName,
            tags,
            row.Revision,
            row.UpdatedAt,
            reasons,
            ReadMissingPropertyKeys(model, row),
            row.SourceReferenceCount);
    }

    private static IReadOnlyList<string> ReadMissingPropertyKeys(
        KnowledgeModelSnapshot model,
        TriageRow row)
    {
        var type = model.KnowledgeTypes.FirstOrDefault(item =>
            item.TypeId == row.KnowledgeTypeId);
        if (type is null)
        {
            return [];
        }
        using var properties = JsonDocument.Parse(row.PropertiesJson);
        return type.Properties
            .Where(item => item.IsEnabled && item.Required)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Where(item =>
                !properties.RootElement.TryGetProperty(item.Key, out var value)
                || value.ValueKind == JsonValueKind.Null)
            .Select(item => item.Key)
            .ToArray();
    }

    private static string NormalizeSearch(string? search) =>
        search?.Trim() ?? string.Empty;

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record TriageCursor(
        int Priority,
        string UpdatedAt,
        string KnowledgeId,
        string StaleAsOf);

    private sealed record TriageRow(
        Guid KnowledgeId,
        string Title,
        Guid KnowledgeTypeId,
        string TypeKey,
        string TypeName,
        DateTimeOffset UpdatedAt,
        string UpdatedAtText,
        int Revision,
        string PropertiesJson,
        bool MissingRequired,
        bool MissingSource,
        bool Stale,
        int SourceReferenceCount,
        int Priority);
}
