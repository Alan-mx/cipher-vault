using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Core.Storage.KnowledgeBase;

public static class SourceStore
{
    public static async Task<PagedResult<SourceSummary>> ListAsync(
        VaultOperationContext context,
        SourceQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = NormalizePageSize(query.PageSize);
        var fingerprint = CursorCodec.Fingerprint(
            "sources",
            query.Status?.ToDatabaseValue(),
            query.Kind?.ToDatabaseValue(),
            query.Query?.Trim().ToUpperInvariant(),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Cursor,
            "sources",
            fingerprint,
            out SourceCursor? cursor);
        if (query.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Source cursor is invalid for this query.");
        }

        using var command = context.CreateCommand();
        var sql = new System.Text.StringBuilder(
            """
            SELECT
                s.source_id,
                s.source_kind,
                s.title,
                s.status,
                r.revision,
                s.updated_at
            FROM sources AS s
            JOIN source_revisions AS r
              ON r.source_revision_id = s.current_revision_id
            WHERE 1 = 1
            """);
        if (query.Status is { } status)
        {
            sql.Append(" AND s.status = $status");
            command.Command.Parameters.AddWithValue(
                "$status",
                status.ToDatabaseValue());
        }
        if (query.Kind is { } kind)
        {
            sql.Append(" AND s.source_kind = $kind");
            command.Command.Parameters.AddWithValue(
                "$kind",
                kind.ToDatabaseValue());
        }
        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            sql.Append(" AND instr(s.title_key, $query) > 0");
            command.Command.Parameters.AddWithValue(
                "$query",
                query.Query.Trim().ToUpperInvariant());
        }
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    s.updated_at < $afterUpdatedAt
                    OR (
                        s.updated_at = $afterUpdatedAt
                        AND s.source_id < $afterId
                    )
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor.UpdatedAt);
            command.Command.Parameters.AddWithValue(
                "$afterId",
                cursor.SourceId);
        }
        sql.Append(
            " ORDER BY s.updated_at DESC, s.source_id DESC LIMIT $limit;");
        command.Command.CommandText = sql.ToString();
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);

        var items = new List<SourceSummary>();
        string lastUpdatedAt = string.Empty;
        string lastId = string.Empty;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SourceSummary(
                Guid.Parse(reader.GetString(0)),
                KnowledgeBaseValueMapping.ParseSourceKind(reader.GetString(1)),
                reader.GetString(2),
                KnowledgeBaseValueMapping.ParseSourceStatus(reader.GetString(3)),
                reader.GetInt32(4),
                ParseTimestamp(reader.GetString(5))));
            lastUpdatedAt = reader.GetString(5);
            lastId = reader.GetString(0);
        }
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new PagedResult<SourceSummary>(
            items,
            hasMore
                ? CursorCodec.Encode(
                    "sources",
                    fingerprint,
                    new SourceCursor(lastUpdatedAt, lastId))
                : null,
            hasMore,
            null);
    }

    public static async Task<SourceDetail> GetAsync(
        VaultOperationContext context,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                s.source_id,
                s.source_kind,
                s.title,
                s.metadata_json,
                s.status,
                r.revision,
                r.source_revision_id,
                s.created_at,
                s.updated_at,
                s.archived_at
            FROM sources AS s
            JOIN source_revisions AS r
              ON r.source_revision_id = s.current_revision_id
            WHERE s.source_id = $sourceId;
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.SourceNotFound,
                "Source was not found.");
        }
        return new SourceDetail(
            Guid.Parse(reader.GetString(0)),
            KnowledgeBaseValueMapping.ParseSourceKind(reader.GetString(1)),
            reader.GetString(2),
            ParseJson(reader.GetString(3)),
            KnowledgeBaseValueMapping.ParseSourceStatus(reader.GetString(4)),
            reader.GetInt32(5),
            Guid.Parse(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)));
    }

    public static async Task<SourceRevisionSummary> GetRevisionAsync(
        VaultOperationContext context,
        Guid sourceId,
        int revision,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                source_id,
                source_revision_id,
                revision,
                source_kind,
                title,
                metadata_json,
                content_format,
                length(content_text),
                created_at
            FROM source_revisions
            WHERE source_id = $sourceId
              AND revision = $revision;
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        command.Command.Parameters.AddWithValue("$revision", revision);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.SourceRevisionNotFound,
                "Source revision was not found.");
        }
        return new SourceRevisionSummary(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            KnowledgeBaseValueMapping.ParseSourceKind(reader.GetString(3)),
            reader.GetString(4),
            ParseJson(reader.GetString(5)),
            KnowledgeBaseValueMapping.ParseContentFormat(reader.GetString(6)),
            reader.GetInt32(7),
            ParseTimestamp(reader.GetString(8)));
    }

    public static async Task<PagedResult<SourceRevisionSummary>>
        ListRevisionsAsync(
            VaultOperationContext context,
            Guid sourceId,
            int pageSize,
            string? pageCursor,
            CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(context, sourceId, cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.SourceNotFound,
                "Source was not found.");
        }
        pageSize = NormalizePageSize(pageSize);
        var fingerprint = CursorCodec.Fingerprint(
            "source-revisions",
            sourceId.ToString("D"),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            pageCursor,
            "source-revisions",
            fingerprint,
            out RevisionCursor? cursor);
        if (pageCursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Source revision cursor is invalid.");
        }

        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                source_id,
                source_revision_id,
                revision,
                source_kind,
                title,
                metadata_json,
                content_format,
                length(content_text),
                created_at
            FROM source_revisions
            WHERE source_id = $sourceId
              AND ($afterRevision IS NULL OR revision < $afterRevision)
            ORDER BY revision DESC
            LIMIT $limit;
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$afterRevision",
            cursor?.Revision ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<SourceRevisionSummary>();
        var lastRevision = 0;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastRevision = reader.GetInt32(2);
            items.Add(new SourceRevisionSummary(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                lastRevision,
                KnowledgeBaseValueMapping.ParseSourceKind(reader.GetString(3)),
                reader.GetString(4),
                ParseJson(reader.GetString(5)),
                KnowledgeBaseValueMapping.ParseContentFormat(reader.GetString(6)),
                reader.GetInt32(7),
                ParseTimestamp(reader.GetString(8))));
        }
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new PagedResult<SourceRevisionSummary>(
            items,
            hasMore
                ? CursorCodec.Encode(
                    "source-revisions",
                    fingerprint,
                    new RevisionCursor(lastRevision))
                : null,
            hasMore,
            null);
    }

    public static async Task<SourceContentChunk> ReadContentAsync(
        VaultOperationContext context,
        Guid sourceId,
        int? revision,
        int offset,
        int maxChars,
        CancellationToken cancellationToken)
    {
        ValidateRead(offset, maxChars);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                r.revision,
                r.content_format,
                r.content_text
            FROM source_revisions AS r
            JOIN sources AS s ON s.source_id = r.source_id
            WHERE r.source_id = $sourceId
              AND r.revision = coalesce(
                  $revision,
                  (SELECT current.revision
                   FROM source_revisions AS current
                   WHERE current.source_revision_id = s.current_revision_id));
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$revision",
            revision ?? (object)DBNull.Value);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                revision is null
                    ? VaultErrorCode.SourceNotFound
                    : VaultErrorCode.SourceRevisionNotFound,
                "Source revision was not found.");
        }
        var content = reader.GetString(2);
        if (offset > content.Length)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Source content offset is invalid.");
        }
        var length = Math.Min(maxChars, content.Length - offset);
        var next = offset + length;
        return new SourceContentChunk(
            sourceId,
            reader.GetInt32(0),
            KnowledgeBaseValueMapping.ParseContentFormat(reader.GetString(1)),
            offset,
            next,
            content.Length,
            content.Substring(offset, length),
            next >= content.Length);
    }

    public static async Task<PagedResult<SourceLocatorView>>
        ListLocatorsAsync(
            VaultOperationContext context,
            Guid sourceId,
            int revision,
            SourceLocatorType? type,
            int pageSize,
            string? pageCursor,
            CancellationToken cancellationToken)
    {
        pageSize = NormalizePageSize(pageSize);
        var revisionId = await ResolveRevisionIdAsync(
            context,
            sourceId,
            revision,
            cancellationToken);
        var fingerprint = CursorCodec.Fingerprint(
            "source-locators",
            sourceId.ToString("D"),
            revision.ToString(CultureInfo.InvariantCulture),
            type?.ToDatabaseValue(),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            pageCursor,
            "source-locators",
            fingerprint,
            out LocatorCursor? cursor);
        if (pageCursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Source locator cursor is invalid.");
        }

        using var command = context.CreateCommand();
        var sql = new System.Text.StringBuilder(
            """
            SELECT
                source_locator_id,
                locator_type,
                locator_value_json,
                label,
                start_offset,
                end_offset,
                ordinal,
                parent_locator_id
            FROM source_locators
            WHERE source_revision_id = $revisionId
            """);
        if (type is { } locatorType)
        {
            sql.Append(" AND locator_type = $locatorType");
            command.Command.Parameters.AddWithValue(
                "$locatorType",
                locatorType.ToDatabaseValue());
        }
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    ordinal > $afterOrdinal
                    OR (ordinal = $afterOrdinal AND source_locator_id > $afterId)
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$afterOrdinal",
                cursor.Ordinal);
            command.Command.Parameters.AddWithValue("$afterId", cursor.Id);
        }
        sql.Append(
            " ORDER BY ordinal, source_locator_id LIMIT $limit;");
        command.Command.CommandText = sql.ToString();
        command.Command.Parameters.AddWithValue("$revisionId", revisionId);
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<SourceLocatorView>();
        var lastOrdinal = 0;
        var lastId = string.Empty;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastOrdinal = reader.GetInt32(6);
            lastId = reader.GetString(0);
            items.Add(new SourceLocatorView(
                Guid.Parse(lastId),
                KnowledgeBaseValueMapping.ParseLocatorType(reader.GetString(1)),
                ParseJson(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                lastOrdinal,
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7))));
        }
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new PagedResult<SourceLocatorView>(
            items,
            hasMore
                ? CursorCodec.Encode(
                    "source-locators",
                    fingerprint,
                    new LocatorCursor(lastOrdinal, lastId))
                : null,
            hasMore,
            null);
    }

    public static async Task<SourceLocatorContent> ReadLocatorAsync(
        VaultOperationContext context,
        Guid sourceId,
        int revision,
        Guid locatorId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken)
    {
        ValidateRead(offset, maxChars);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                l.source_locator_id,
                l.locator_type,
                l.locator_value_json,
                l.label,
                l.start_offset,
                l.end_offset,
                l.ordinal,
                l.parent_locator_id,
                r.content_text
            FROM source_locators AS l
            JOIN source_revisions AS r
              ON r.source_revision_id = l.source_revision_id
            JOIN sources AS s ON s.source_id = r.source_id
            WHERE r.source_id = $sourceId
              AND r.revision = $revision
              AND l.source_locator_id = $locatorId;
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        command.Command.Parameters.AddWithValue("$revision", revision);
        command.Command.Parameters.AddWithValue(
            "$locatorId",
            locatorId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.SourceLocatorNotFound,
                "Source locator was not found.");
        }
        var start = reader.GetInt32(4);
        var end = reader.GetInt32(5);
        var length = end - start;
        if (offset > length)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Locator offset is invalid.");
        }
        var take = Math.Min(maxChars, length - offset);
        var next = offset + take;
        return new SourceLocatorContent(
            sourceId,
            revision,
            new SourceLocatorView(
                Guid.Parse(reader.GetString(0)),
                KnowledgeBaseValueMapping.ParseLocatorType(reader.GetString(1)),
                ParseJson(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                start,
                end,
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7))),
            offset,
            next,
            length,
            reader.GetString(8).Substring(start + offset, take),
            next >= length);
    }

    private static async Task<bool> ExistsAsync(
        VaultOperationContext context,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            "SELECT count(*) FROM sources WHERE source_id = $sourceId;";
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<string> ResolveRevisionIdAsync(
        VaultOperationContext context,
        Guid sourceId,
        int revision,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT source_revision_id
            FROM source_revisions
            WHERE source_id = $sourceId AND revision = $revision;
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        command.Command.Parameters.AddWithValue("$revision", revision);
        var value = await command.Command.ExecuteScalarAsync(cancellationToken);
        return value is string id
            ? id
            : throw new VaultException(
                VaultErrorCode.SourceRevisionNotFound,
                "Source revision was not found.");
    }

    private static void ValidateRead(int offset, int maxChars)
    {
        if (offset < 0)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Offset must not be negative.");
        }
        if (maxChars is < 2 or > 65_536)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "maxChars must be between 2 and 65536.");
        }
    }

    private static int NormalizePageSize(int pageSize) =>
        pageSize is < 1 or > 100
            ? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "pageSize must be between 1 and 100.")
            : pageSize;

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record SourceCursor(string UpdatedAt, string SourceId);
    private sealed record RevisionCursor(int Revision);
    private sealed record LocatorCursor(int Ordinal, string Id);
}
