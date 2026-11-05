using System.Globalization;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Core.Storage.KnowledgeBase;

public static class KnowledgeStore
{
    public static async Task<PagedResult<KnowledgeSummary>> ListAsync(
        VaultOperationContext context,
        KnowledgeQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = NormalizePageSize(query.PageSize);
        var tags = NormalizeTags(query.Tags);
        var fingerprint = CursorCodec.Fingerprint(
            "knowledge",
            query.Status?.ToDatabaseValue(),
            query.TypeId?.ToString("D"),
            string.Join(',', tags),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Cursor,
            "knowledge",
            fingerprint,
            out KnowledgeTimeCursor? cursor);
        if (query.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Knowledge cursor is invalid for this query.");
        }

        List<SummaryRow> rows;
        using (var command = context.CreateCommand())
        {
            var sql = new StringBuilder(
                """
                SELECT
                    k.knowledge_id,
                    k.title,
                    k.summary,
                    k.knowledge_type_id,
                    t.type_key,
                    k.status,
                    r.revision,
                    k.updated_at
                FROM knowledge_items AS k
                JOIN knowledge_types AS t
                  ON t.type_id = k.knowledge_type_id
                JOIN knowledge_revisions AS r
                  ON r.knowledge_revision_id = k.current_revision_id
                WHERE 1 = 1
                """);
            if (query.Status is { } status)
            {
                sql.Append(" AND k.status = $status");
                command.Command.Parameters.AddWithValue(
                    "$status",
                    status.ToDatabaseValue());
            }
            if (query.TypeId is { } typeId)
            {
                sql.Append(" AND k.knowledge_type_id = $typeId");
                command.Command.Parameters.AddWithValue(
                    "$typeId",
                    typeId.ToString("D"));
            }
            AppendTagFilters(sql, command, tags);
            if (cursor is not null)
            {
                sql.Append(
                    """
                     AND (
                        k.updated_at < $afterUpdatedAt
                        OR (k.updated_at = $afterUpdatedAt
                            AND k.knowledge_id < $afterId)
                     )
                    """);
                command.Command.Parameters.AddWithValue(
                    "$afterUpdatedAt",
                    cursor.UpdatedAt);
                command.Command.Parameters.AddWithValue("$afterId", cursor.Id);
            }
            sql.Append(
                " ORDER BY k.updated_at DESC, k.knowledge_id DESC LIMIT $limit;");
            command.Command.CommandText = sql.ToString();
            command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
            rows = await ReadSummaryRowsAsync(
                command.Command,
                cancellationToken);
        }
        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var items = await AttachTagsAsync(
            context,
            rows,
            cancellationToken);
        var last = rows.Count > 0 ? rows[^1] : null;
        return new PagedResult<KnowledgeSummary>(
            items,
            hasMore && last is not null
                ? CursorCodec.Encode(
                    "knowledge",
                    fingerprint,
                    new KnowledgeTimeCursor(
                        last.UpdatedAtText,
                        last.KnowledgeId.ToString("D")))
                : null,
            hasMore,
            null);
    }

    public static async Task<PagedResult<KnowledgeSearchHit>> SearchAsync(
        VaultOperationContext context,
        KnowledgeSearchQuery query,
        CancellationToken cancellationToken)
    {
        var trimmed = query.Query.Trim();
        if (trimmed.Length == 0
            && query.TypeIds is not { Count: > 0 }
            && query.SourceIds is not { Count: > 0 }
            && query.Tags is not { Count: > 0 })
        {
            throw new VaultException(
                VaultErrorCode.SearchQueryRequired,
                "A search query or filter is required.");
        }
        if (query.Mode == KnowledgeSearchMode.Fulltext
            && trimmed.EnumerateRunes().Count() < 3)
        {
            throw new VaultException(
                VaultErrorCode.SearchQueryTooShort,
                "Fulltext query must contain at least three characters.");
        }
        var pageSize = NormalizePageSize(query.PageSize);
        var tags = NormalizeTags(query.Tags);
        var fingerprint = CursorCodec.Fingerprint(
            "knowledge-search",
            query.Mode.ToString(),
            trimmed.ToUpperInvariant(),
            query.TypeIds is null
                ? null
                : string.Join(
                    ',',
                    query.TypeIds
                        .OrderBy(item => item)
                        .Select(item => item.ToString("D"))),
            query.SourceIds is null
                ? null
                : string.Join(
                    ',',
                    query.SourceIds
                        .OrderBy(item => item)
                        .Select(item => item.ToString("D"))),
            string.Join(',', tags),
            query.UpdatedAfter?.ToUniversalTime().ToString("O"),
            query.UpdatedBefore?.ToUniversalTime().ToString("O"),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Cursor,
            "knowledge-search",
            fingerprint,
            out SearchCursor? cursor);
        if (query.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Knowledge search cursor is invalid.");
        }

        return query.Mode switch
        {
            KnowledgeSearchMode.Keyword => await SearchKeywordAsync(
                context,
                query,
                trimmed,
                tags,
                pageSize,
                fingerprint,
                cursor,
                cancellationToken),
            KnowledgeSearchMode.Fulltext => await SearchFulltextAsync(
                context,
                query,
                trimmed,
                tags,
                pageSize,
                fingerprint,
                cursor,
                cancellationToken),
            _ => throw new VaultException(
                VaultErrorCode.InvalidSearchMode,
                "Search mode is invalid.")
        };
    }

    public static async Task<KnowledgeDetail> GetAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        CancellationToken cancellationToken)
    {
        Guid storedKnowledgeId;
        string title;
        string summary;
        Guid knowledgeTypeId;
        string knowledgeTypeKey;
        Guid? templateId;
        KnowledgeStatus status;
        int revision;
        Guid revisionId;
        JsonElement properties;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                SELECT
                    k.knowledge_id,
                    k.title,
                    k.summary,
                    k.knowledge_type_id,
                    t.type_key,
                    r.template_id,
                    k.status,
                    r.revision,
                    r.knowledge_revision_id,
                    r.properties_json,
                    k.created_at,
                    k.updated_at
                FROM knowledge_items AS k
                JOIN knowledge_types AS t
                  ON t.type_id = k.knowledge_type_id
                JOIN knowledge_revisions AS r
                  ON r.knowledge_revision_id = k.current_revision_id
                WHERE k.knowledge_id = $knowledgeId;
                """;
            command.Command.Parameters.AddWithValue(
                "$knowledgeId",
                knowledgeId.ToString("D"));
            await using var reader =
                await command.Command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new VaultException(
                    VaultErrorCode.KnowledgeNotFound,
                    "Knowledge was not found.");
            }
            storedKnowledgeId = Guid.Parse(reader.GetString(0));
            title = reader.GetString(1);
            summary = reader.GetString(2);
            knowledgeTypeId = Guid.Parse(reader.GetString(3));
            knowledgeTypeKey = reader.GetString(4);
            templateId = reader.IsDBNull(5)
                ? null
                : Guid.Parse(reader.GetString(5));
            status = KnowledgeBaseValueMapping.ParseKnowledgeStatus(
                reader.GetString(6));
            revision = reader.GetInt32(7);
            revisionId = Guid.Parse(reader.GetString(8));
            properties = ParseJson(reader.GetString(9));
            createdAt = ParseTimestamp(reader.GetString(10));
            updatedAt = ParseTimestamp(reader.GetString(11));
        }
        return new KnowledgeDetail(
            storedKnowledgeId,
            title,
            summary,
            knowledgeTypeId,
            knowledgeTypeKey,
            templateId,
            status,
            revision,
            revisionId,
            properties,
            await ReadTagsAsync(context, knowledgeId, cancellationToken),
            await ReadSourceReferencesAsync(
                context,
                revisionId,
                null,
                cancellationToken),
            createdAt,
            updatedAt);
    }

    public static async Task<KnowledgeRevisionDetail> GetRevisionAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        int revision,
        CancellationToken cancellationToken)
    {
        Guid storedKnowledgeId;
        KnowledgeStatus status;
        Guid revisionId;
        int storedRevision;
        string title;
        string summary;
        Guid typeId;
        string typeKey;
        Guid? templateId;
        int modelVersion;
        JsonElement properties;
        IReadOnlyList<string> tags;
        DateTimeOffset createdAt;
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                SELECT
                    k.knowledge_id,
                    k.status,
                    r.knowledge_revision_id,
                    r.revision,
                    r.title,
                    r.summary,
                    r.knowledge_type_id,
                    t.type_key,
                    r.template_id,
                    r.knowledge_model_version,
                    r.properties_json,
                    r.tags_json,
                    r.created_at
                FROM knowledge_items AS k
                JOIN knowledge_revisions AS r
                  ON r.knowledge_id = k.knowledge_id
                JOIN knowledge_types AS t
                  ON t.type_id = r.knowledge_type_id
                WHERE r.knowledge_id = $knowledgeId
                  AND r.revision = $revision;
                """;
            command.Command.Parameters.AddWithValue(
                "$knowledgeId",
                knowledgeId.ToString("D"));
            command.Command.Parameters.AddWithValue("$revision", revision);
            await using (var reader =
                         await command.Command.ExecuteReaderAsync(
                             cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new VaultException(
                        VaultErrorCode.KnowledgeRevisionNotFound,
                        "Knowledge revision was not found.");
                }
                storedKnowledgeId = Guid.Parse(reader.GetString(0));
                status = KnowledgeBaseValueMapping.ParseKnowledgeStatus(
                    reader.GetString(1));
                revisionId = Guid.Parse(reader.GetString(2));
                storedRevision = reader.GetInt32(3);
                title = reader.GetString(4);
                summary = reader.GetString(5);
                typeId = Guid.Parse(reader.GetString(6));
                typeKey = reader.GetString(7);
                templateId = reader.IsDBNull(8)
                    ? null
                    : Guid.Parse(reader.GetString(8));
                modelVersion = reader.GetInt32(9);
                properties = ParseJson(reader.GetString(10));
                tags = ParseJson(reader.GetString(11))
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray();
                createdAt = ParseTimestamp(reader.GetString(12));
            }
        }
        return new KnowledgeRevisionDetail(
            storedKnowledgeId,
            status,
            new KnowledgeRevisionSummary(
                revisionId,
                storedRevision,
                title,
                typeId,
                templateId,
                modelVersion,
                createdAt),
            title,
            summary,
            typeId,
            typeKey,
            templateId,
            properties,
            tags,
            await ReadSourceReferencesAsync(
                context,
                revisionId,
                null,
                cancellationToken));
    }

    public static async Task<PagedResult<KnowledgeRevisionSummary>>
        ListRevisionsAsync(
            VaultOperationContext context,
            Guid knowledgeId,
            int pageSize,
            string? pageCursor,
            CancellationToken cancellationToken)
    {
        pageSize = NormalizePageSize(pageSize);
        var fingerprint = CursorCodec.Fingerprint(
            "knowledge-revisions",
            knowledgeId.ToString("D"),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            pageCursor,
            "knowledge-revisions",
            fingerprint,
            out RevisionCursor? cursor);
        if (pageCursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Knowledge revision cursor is invalid.");
        }
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                knowledge_revision_id,
                revision,
                title,
                knowledge_type_id,
                template_id,
                knowledge_model_version,
                created_at
            FROM knowledge_revisions
            WHERE knowledge_id = $knowledgeId
              AND ($afterRevision IS NULL OR revision < $afterRevision)
            ORDER BY revision DESC
            LIMIT $limit;
            """;
        command.Command.Parameters.AddWithValue(
            "$knowledgeId",
            knowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$afterRevision",
            cursor?.Revision ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<KnowledgeRevisionSummary>();
        var lastRevision = 0;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastRevision = reader.GetInt32(1);
            items.Add(new KnowledgeRevisionSummary(
                Guid.Parse(reader.GetString(0)),
                lastRevision,
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.GetInt32(5),
                ParseTimestamp(reader.GetString(6))));
        }
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new PagedResult<KnowledgeRevisionSummary>(
            items,
            hasMore
                ? CursorCodec.Encode(
                    "knowledge-revisions",
                    fingerprint,
                    new RevisionCursor(lastRevision))
                : null,
            hasMore,
            null);
    }

    public static async Task<KnowledgeContentChunk> ReadContentAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        int? revision,
        int offset,
        int maxChars,
        CancellationToken cancellationToken)
    {
        ValidateRead(offset, maxChars);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT r.revision, r.content_markdown
            FROM knowledge_revisions AS r
            JOIN knowledge_items AS k ON k.knowledge_id = r.knowledge_id
            WHERE r.knowledge_id = $knowledgeId
              AND r.revision = coalesce(
                  $revision,
                  (SELECT current.revision
                   FROM knowledge_revisions AS current
                   WHERE current.knowledge_revision_id =
                         k.current_revision_id));
            """;
        command.Command.Parameters.AddWithValue(
            "$knowledgeId",
            knowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$revision",
            revision ?? (object)DBNull.Value);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                revision is null
                    ? VaultErrorCode.KnowledgeNotFound
                    : VaultErrorCode.KnowledgeRevisionNotFound,
                "Knowledge revision was not found.");
        }
        var content = reader.GetString(1);
        if (offset > content.Length)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Knowledge content offset is invalid.");
        }
        var length = Math.Min(maxChars, content.Length - offset);
        var next = offset + length;
        return new KnowledgeContentChunk(
            knowledgeId,
            reader.GetInt32(0),
            offset,
            next,
            content.Length,
            content.Substring(offset, length),
            next >= content.Length);
    }

    public static async Task<KnowledgeDocument> ExportDocumentAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        int revision,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                r.title,
                t.type_key,
                r.tags_json,
                r.properties_json,
                r.content_markdown
            FROM knowledge_revisions AS r
            JOIN knowledge_types AS t ON t.type_id = r.knowledge_type_id
            WHERE r.knowledge_id = $knowledgeId AND r.revision = $revision;
            """;
        command.Command.Parameters.AddWithValue(
            "$knowledgeId",
            knowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue("$revision", revision);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.KnowledgeRevisionNotFound,
                "Knowledge revision was not found.");
        }
        var title = reader.GetString(0);
        var tags = ParseJson(reader.GetString(2));
        var frontmatter = new StringBuilder();
        frontmatter.AppendLine("---");
        frontmatter.Append("type: ").AppendLine(reader.GetString(1));
        frontmatter.Append("tags: ")
            .AppendLine(tags.GetRawText());
        var properties = ParseJson(reader.GetString(3));
        foreach (var property in properties.EnumerateObject())
        {
            frontmatter.Append(property.Name)
                .Append(": ")
                .AppendLine(property.Value.GetRawText());
        }
        frontmatter.AppendLine("---");
        frontmatter.AppendLine(reader.GetString(4));
        return new KnowledgeDocument(
            knowledgeId,
            revision,
            SanitizeFileName(title) + ".md",
            frontmatter.ToString());
    }

    public static async Task<PagedResult<KnowledgeRelationItem>>
        ListRelationsAsync(
            VaultOperationContext context,
            Guid knowledgeId,
            KnowledgeRelationStatus? status,
            int pageSize,
            string? pageCursor,
            CancellationToken cancellationToken)
    {
        pageSize = NormalizePageSize(pageSize);
        var fingerprint = CursorCodec.Fingerprint(
            "knowledge-relations",
            knowledgeId.ToString("D"),
            status?.ToString(),
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            pageCursor,
            "knowledge-relations",
            fingerprint,
            out RelationCursor? cursor);
        if (pageCursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Relation cursor is invalid.");
        }
        using var command = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT
                r.relation_id,
                r.relation_type_id,
                t.relation_key,
                r.source_knowledge_id,
                r.target_knowledge_id,
                rv.source_knowledge_revision_id,
                rv.target_knowledge_revision_id,
                source_r.revision,
                target_r.revision,
                r.status,
                rv.rationale,
                r.created_at,
                r.updated_at
            FROM knowledge_relations AS r
            JOIN relation_types AS t ON t.relation_type_id = r.relation_type_id
            JOIN knowledge_relation_revisions AS rv
              ON rv.relation_revision_id = r.current_revision_id
            JOIN knowledge_revisions AS source_r
              ON source_r.knowledge_revision_id =
                 rv.source_knowledge_revision_id
            JOIN knowledge_revisions AS target_r
              ON target_r.knowledge_revision_id =
                 rv.target_knowledge_revision_id
            WHERE (r.source_knowledge_id = $knowledgeId
                OR r.target_knowledge_id = $knowledgeId)
            """);
        command.Command.Parameters.AddWithValue(
            "$knowledgeId",
            knowledgeId.ToString("D"));
        if (status is { } relationStatus)
        {
            sql.Append(" AND r.status = $status");
            command.Command.Parameters.AddWithValue(
                "$status",
                relationStatus == KnowledgeRelationStatus.Active
                    ? "active"
                    : "retracted");
        }
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    r.updated_at < $afterUpdatedAt
                    OR (r.updated_at = $afterUpdatedAt
                        AND r.relation_id < $afterId)
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor.UpdatedAt);
            command.Command.Parameters.AddWithValue("$afterId", cursor.Id);
        }
        sql.Append(
            " ORDER BY r.updated_at DESC, r.relation_id DESC LIMIT $limit;");
        command.Command.CommandText = sql.ToString();
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<KnowledgeRelationItem>();
        string lastUpdated = string.Empty;
        string lastId = string.Empty;
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastUpdated = reader.GetString(12);
            lastId = reader.GetString(0);
            items.Add(new KnowledgeRelationItem(
                Guid.Parse(lastId),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                Guid.Parse(reader.GetString(4)),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9) == "active"
                    ? KnowledgeRelationStatus.Active
                    : KnowledgeRelationStatus.Retracted,
                reader.GetString(10),
                ParseTimestamp(reader.GetString(11)),
                ParseTimestamp(reader.GetString(12))));
        }
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        return new PagedResult<KnowledgeRelationItem>(
            items,
            hasMore
                ? CursorCodec.Encode(
                    "knowledge-relations",
                    fingerprint,
                    new RelationCursor(lastUpdated, lastId))
                : null,
            hasMore,
            null);
    }

    public static async Task<LocalKnowledgeGraph> GetLocalGraphAsync(
        VaultOperationContext context,
        Guid centerId,
        int depth,
        int maxNodes,
        int maxEdges,
        CancellationToken cancellationToken)
    {
        if (depth is < 1 or > 4
            || maxNodes is < 1 or > 1000
            || maxEdges is < 1 or > 2000)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Graph limits are invalid.");
        }
        var nodeIds = new HashSet<Guid> { centerId };
        var frontier = new HashSet<Guid> { centerId };
        var edges = new Dictionary<Guid, LocalKnowledgeGraphEdge>();
        var truncated = false;
        for (var currentDepth = 1;
             currentDepth <= depth && frontier.Count > 0;
             currentDepth++)
        {
            var ids = frontier.ToArray();
            frontier = [];
            using var command = context.CreateCommand();
            var parameters = ids.Select((_, index) => $"$center{index}")
                .ToArray();
            command.Command.CommandText =
                $"""
                 SELECT
                    r.relation_id,
                    r.relation_type_id,
                    r.source_knowledge_id,
                    r.target_knowledge_id,
                    t.is_directed
                 FROM knowledge_relations AS r
                 JOIN relation_types AS t
                   ON t.relation_type_id = r.relation_type_id
                 WHERE r.status = 'active'
                   AND (
                    r.source_knowledge_id IN ({string.Join(',', parameters)})
                    OR r.target_knowledge_id IN ({string.Join(',', parameters)})
                   )
                 ORDER BY r.relation_id
                 LIMIT $limit;
                 """;
            for (var index = 0; index < ids.Length; index++)
            {
                command.Command.Parameters.AddWithValue(
                    parameters[index],
                    ids[index].ToString("D"));
            }
            command.Command.Parameters.AddWithValue(
                "$limit",
                maxEdges + 1);
            await using var reader =
                await command.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (edges.Count >= maxEdges)
                {
                    truncated = true;
                    break;
                }
                var relationId = Guid.Parse(reader.GetString(0));
                var sourceId = Guid.Parse(reader.GetString(2));
                var targetId = Guid.Parse(reader.GetString(3));
                edges.TryAdd(
                    relationId,
                    new LocalKnowledgeGraphEdge(
                        relationId,
                        Guid.Parse(reader.GetString(1)),
                        sourceId,
                        targetId,
                        reader.GetInt32(4) == 1));
                foreach (var next in new[] { sourceId, targetId })
                {
                    if (nodeIds.Count >= maxNodes)
                    {
                        truncated = true;
                        continue;
                    }
                    if (nodeIds.Add(next))
                    {
                        frontier.Add(next);
                    }
                }
            }
        }
        if (edges.Count == 0 && !nodeIds.Contains(centerId))
        {
            throw new VaultException(
                VaultErrorCode.KnowledgeNotFound,
                "Knowledge was not found.");
        }
        var nodes = await ReadGraphNodesAsync(
            context,
            centerId,
            nodeIds,
            edges.Values,
            cancellationToken);
        return new LocalKnowledgeGraph(
            nodes,
            edges.Values.OrderBy(item => item.RelationId).ToArray(),
            truncated);
    }

    private static async Task<PagedResult<KnowledgeSearchHit>>
        SearchKeywordAsync(
            VaultOperationContext context,
            KnowledgeSearchQuery query,
            string normalizedQuery,
            IReadOnlyList<string> tags,
            int pageSize,
            string fingerprint,
            SearchCursor? cursor,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT
                k.knowledge_id,
                k.title,
                k.summary,
                k.knowledge_type_id,
                t.type_key,
                k.status,
                r.revision,
                r.knowledge_revision_id,
                k.updated_at
            FROM knowledge_items AS k
            JOIN knowledge_types AS t ON t.type_id = k.knowledge_type_id
            JOIN knowledge_revisions AS r
              ON r.knowledge_revision_id = k.current_revision_id
            WHERE k.status = 'active'
            """);
        if (normalizedQuery.Length > 0)
        {
            sql.Append(
                """
                 AND (
                    k.title_key LIKE $titlePrefix ESCAPE '\'
                    OR EXISTS (
                        SELECT 1 FROM knowledge_tags AS kt
                        WHERE kt.knowledge_id = k.knowledge_id
                          AND kt.tag_key = $tagQuery
                    )
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$titlePrefix",
                EscapeLike(normalizedQuery.ToUpperInvariant()) + "%");
            command.Command.Parameters.AddWithValue(
                "$tagQuery",
                normalizedQuery.ToUpperInvariant());
        }
        AppendSearchFilters(
            sql,
            command,
            query,
            tags);
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    k.updated_at < $afterUpdatedAt
                    OR (k.updated_at = $afterUpdatedAt
                        AND k.knowledge_id < $afterId)
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor.UpdatedAt);
            command.Command.Parameters.AddWithValue("$afterId", cursor.Id);
        }
        sql.Append(
            " ORDER BY k.updated_at DESC, k.knowledge_id DESC LIMIT $limit;");
        command.Command.CommandText = sql.ToString();
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var rows = new List<SearchRow>();
        await using (var reader =
                     await command.Command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadSearchRow(reader, 0));
            }
        }
        command.Dispose();
        return await BuildSearchPageAsync(
            context,
            query.Mode,
            pageSize,
            fingerprint,
            rows,
            cancellationToken);
    }

    private static async Task<PagedResult<KnowledgeSearchHit>>
        SearchFulltextAsync(
            VaultOperationContext context,
            KnowledgeSearchQuery query,
            string normalizedQuery,
            IReadOnlyList<string> tags,
            int pageSize,
            string fingerprint,
            SearchCursor? cursor,
            CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT
                k.knowledge_id,
                k.title,
                k.summary,
                k.knowledge_type_id,
                t.type_key,
                k.status,
                r.revision,
                r.knowledge_revision_id,
                k.updated_at,
                bm25(knowledge_fts) AS rank
            FROM knowledge_fts
            JOIN knowledge_items AS k
              ON k.knowledge_id = knowledge_fts.knowledge_id
            JOIN knowledge_types AS t ON t.type_id = k.knowledge_type_id
            JOIN knowledge_revisions AS r
              ON r.knowledge_revision_id = k.current_revision_id
            WHERE knowledge_fts MATCH $match
              AND k.status = 'active'
            """);
        AppendSearchFilters(sql, command, query, tags);
        if (cursor is not null)
        {
            sql.Append(
                """
                 AND (
                    bm25(knowledge_fts) > $afterRank
                    OR (bm25(knowledge_fts) = $afterRank
                        AND (
                            k.updated_at < $afterUpdatedAt
                            OR (k.updated_at = $afterUpdatedAt
                                AND k.knowledge_id < $afterId)
                        ))
                 )
                """);
            command.Command.Parameters.AddWithValue(
                "$afterRank",
                cursor.Rank ?? 0);
            command.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor.UpdatedAt);
            command.Command.Parameters.AddWithValue("$afterId", cursor.Id);
        }
        sql.Append(
            """
             ORDER BY rank ASC, k.updated_at DESC, k.knowledge_id DESC
             LIMIT $limit;
            """);
        command.Command.CommandText = sql.ToString();
        command.Command.Parameters.AddWithValue(
            "$match",
            $"\"{normalizedQuery.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        command.Command.Parameters.AddWithValue("$limit", pageSize + 1);
        var rows = new List<SearchRow>();
        await using (var reader =
                     await command.Command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadSearchRow(reader, reader.GetDouble(9)));
            }
        }
        command.Dispose();
        return await BuildSearchPageAsync(
            context,
            query.Mode,
            pageSize,
            fingerprint,
            rows,
            cancellationToken);
    }

    private static async Task<PagedResult<KnowledgeSearchHit>>
        BuildSearchPageAsync(
            VaultOperationContext context,
            KnowledgeSearchMode mode,
            int pageSize,
            string fingerprint,
            List<SearchRow> rows,
            CancellationToken cancellationToken)
    {
        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var tagsByKnowledge = await ReadTagsManyAsync(
            context,
            rows.Select(item => item.KnowledgeId).ToArray(),
            cancellationToken);
        var sourceRefs = await ReadSourceReferencesManyAsync(
            context,
            rows.Select(item => item.RevisionId).ToArray(),
            cancellationToken);
        var items = rows.Select(row => new KnowledgeSearchHit(
                row.KnowledgeId,
                row.Title,
                row.KnowledgeTypeId,
                row.KnowledgeTypeKey,
                row.Summary,
                tagsByKnowledge.GetValueOrDefault(row.KnowledgeId, []),
                sourceRefs.GetValueOrDefault(row.RevisionId, []),
                row.Revision,
                row.UpdatedAt))
            .ToArray();
        var last = rows.Count > 0 ? rows[^1] : null;
        return new PagedResult<KnowledgeSearchHit>(
            items,
            hasMore && last is not null
                ? CursorCodec.Encode(
                    "knowledge-search",
                    fingerprint,
                    new SearchCursor(
                        last.UpdatedAtText,
                        last.KnowledgeId.ToString("D"),
                        mode == KnowledgeSearchMode.Fulltext
                            ? last.Rank
                            : null))
                : null,
            hasMore,
            null);
    }

    private static void AppendSearchFilters(
        StringBuilder sql,
        VaultCommandLease command,
        KnowledgeSearchQuery query,
        IReadOnlyList<string> tags)
    {
        if (query.TypeIds is { Count: > 0 })
        {
            var parameters = query.TypeIds
                .Select((_, index) => $"$type{index}")
                .ToArray();
            sql.Append(
                $" AND k.knowledge_type_id IN ({string.Join(',', parameters)})");
            for (var index = 0; index < parameters.Length; index++)
            {
                command.Command.Parameters.AddWithValue(
                    parameters[index],
                    query.TypeIds[index].ToString("D"));
            }
        }
        if (query.SourceIds is { Count: > 0 })
        {
            var parameters = query.SourceIds
                .Select((_, index) => $"$source{index}")
                .ToArray();
            sql.Append(
                $"""
                  AND EXISTS (
                    SELECT 1 FROM source_references AS sr
                    WHERE sr.knowledge_revision_id = r.knowledge_revision_id
                      AND sr.source_id IN ({string.Join(',', parameters)})
                  )
                 """);
            for (var index = 0; index < parameters.Length; index++)
            {
                command.Command.Parameters.AddWithValue(
                    parameters[index],
                    query.SourceIds[index].ToString("D"));
            }
        }
        AppendTagFilters(sql, command, tags);
        if (query.UpdatedAfter is { } after)
        {
            sql.Append(" AND k.updated_at >= $updatedAfter");
            command.Command.Parameters.AddWithValue(
                "$updatedAfter",
                after.ToUniversalTime().ToString("O"));
        }
        if (query.UpdatedBefore is { } before)
        {
            sql.Append(" AND k.updated_at < $updatedBefore");
            command.Command.Parameters.AddWithValue(
                "$updatedBefore",
                before.ToUniversalTime().ToString("O"));
        }
    }

    private static void AppendTagFilters(
        StringBuilder sql,
        VaultCommandLease command,
        IReadOnlyList<string> tags)
    {
        for (var index = 0; index < tags.Count; index++)
        {
            var parameter = $"$filterTag{index}";
            sql.Append(
                $"""
                  AND EXISTS (
                    SELECT 1 FROM knowledge_tags AS filtered
                    WHERE filtered.knowledge_id = k.knowledge_id
                      AND filtered.tag_key = {parameter}
                  )
                 """);
            command.Command.Parameters.AddWithValue(parameter, tags[index]);
        }
    }

    private static async Task<List<SummaryRow>> ReadSummaryRowsAsync(
        Microsoft.Data.Sqlite.SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<SummaryRow>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SummaryRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                KnowledgeBaseValueMapping.ParseKnowledgeStatus(
                    reader.GetString(5)),
                reader.GetInt32(6),
                ParseTimestamp(reader.GetString(7)),
                reader.GetString(7)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<KnowledgeSummary>> AttachTagsAsync(
        VaultOperationContext context,
        IReadOnlyList<SummaryRow> rows,
        CancellationToken cancellationToken)
    {
        var tags = await ReadTagsManyAsync(
            context,
            rows.Select(item => item.KnowledgeId).ToArray(),
            cancellationToken);
        return rows.Select(row => new KnowledgeSummary(
                row.KnowledgeId,
                row.Title,
                row.Summary,
                row.KnowledgeTypeId,
                row.KnowledgeTypeKey,
                tags.GetValueOrDefault(row.KnowledgeId, []),
                row.Status,
                row.Revision,
                row.UpdatedAt))
            .ToArray();
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>>
        ReadTagsManyAsync(
            VaultOperationContext context,
            IReadOnlyCollection<Guid> knowledgeIds,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<string>>();
        if (knowledgeIds.Count == 0)
        {
            return [];
        }
        using var command = context.CreateCommand();
        var parameters = knowledgeIds
            .Select((_, index) => $"$id{index}")
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

    private static async Task<IReadOnlyList<string>> ReadTagsAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        CancellationToken cancellationToken)
    {
        var values = await ReadTagsManyAsync(
            context,
            [knowledgeId],
            cancellationToken);
        return values.GetValueOrDefault(knowledgeId, []);
    }

    private static async Task<IReadOnlyList<SourceReferenceView>>
        ReadSourceReferencesAsync(
            VaultOperationContext context,
            Guid revisionId,
            int? excerptLimit,
            CancellationToken cancellationToken)
    {
        var values = await ReadSourceReferencesManyAsync(
            context,
            [revisionId],
            cancellationToken);
        var items = values.GetValueOrDefault(revisionId, []);
        return excerptLimit is null
            ? items
            : items.Select(item => item with
            {
                Excerpt = item.Excerpt.Length <= excerptLimit.Value
                    ? item.Excerpt
                    : item.Excerpt[..excerptLimit.Value]
            }).ToArray();
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<SourceReferenceView>>>
        ReadSourceReferencesManyAsync(
            VaultOperationContext context,
            IReadOnlyCollection<Guid> revisionIds,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<SourceReferenceView>>();
        if (revisionIds.Count == 0)
        {
            return [];
        }
        using var command = context.CreateCommand();
        var parameters = revisionIds
            .Select((_, index) => $"$revision{index}")
            .ToArray();
        command.Command.CommandText =
            $"""
             SELECT
                source_reference_id,
                knowledge_revision_id,
                source_id,
                source_revision,
                locator_type,
                locator_value_json,
                excerpt,
                extraction_metadata_json
             FROM source_references
             WHERE knowledge_revision_id IN ({string.Join(',', parameters)})
             ORDER BY knowledge_revision_id, source_reference_order;
             """;
        var index = 0;
        foreach (var id in revisionIds)
        {
            command.Command.Parameters.AddWithValue(
                parameters[index++],
                id.ToString("D"));
        }
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var revisionId = Guid.Parse(reader.GetString(1));
            if (!result.TryGetValue(revisionId, out var items))
            {
                items = [];
                result[revisionId] = items;
            }
            items.Add(new SourceReferenceView(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(2)),
                reader.GetInt32(3),
                KnowledgeBaseValueMapping.ParseLocatorType(reader.GetString(4)),
                ParseJson(reader.GetString(5)),
                reader.GetString(6),
                ParseJson(reader.GetString(7))));
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SourceReferenceView>)pair.Value);
    }

    private static SearchRow ReadSearchRow(
        Microsoft.Data.Sqlite.SqliteDataReader reader,
        double rank) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            KnowledgeBaseValueMapping.ParseKnowledgeStatus(reader.GetString(5)),
            reader.GetInt32(6),
            Guid.Parse(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)),
            reader.GetString(8),
            rank);

    private static async Task<IReadOnlyList<LocalKnowledgeGraphNode>>
        ReadGraphNodesAsync(
            VaultOperationContext context,
            Guid centerId,
            IReadOnlyCollection<Guid> ids,
            IEnumerable<LocalKnowledgeGraphEdge> edges,
            CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var degrees = edges
            .SelectMany(edge => new[]
            {
                edge.SourceKnowledgeId,
                edge.TargetKnowledgeId
            })
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());
        using var command = context.CreateCommand();
        var parameters = ids.Select((_, index) => $"$id{index}").ToArray();
        command.Command.CommandText =
            $"""
             SELECT
                k.knowledge_id,
                k.title,
                k.knowledge_type_id,
                r.revision
             FROM knowledge_items AS k
             JOIN knowledge_revisions AS r
               ON r.knowledge_revision_id = k.current_revision_id
             WHERE k.knowledge_id IN ({string.Join(',', parameters)})
             ORDER BY k.title_key, k.knowledge_id;
             """;
        var index = 0;
        foreach (var id in ids)
        {
            command.Command.Parameters.AddWithValue(
                parameters[index++],
                id.ToString("D"));
        }
        var tags = await ReadTagsManyAsync(
            context,
            ids.ToArray(),
            cancellationToken);
        var nodes = new List<LocalKnowledgeGraphNode>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            nodes.Add(new LocalKnowledgeGraphNode(
                id,
                reader.GetString(1),
                Guid.Parse(reader.GetString(2)),
                tags.GetValueOrDefault(id, []),
                reader.GetInt32(3),
                degrees.GetValueOrDefault(id),
                id == centerId ? 0 : 1,
                id == centerId));
        }
        return nodes;
    }

    private static async Task<int> ReadRevisionNumberAsync(
        VaultOperationContext context,
        string revisionId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT revision FROM knowledge_revisions
            WHERE knowledge_revision_id = $revisionId;
            """;
        command.Command.Parameters.AddWithValue("$revisionId", revisionId);
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken));
    }

    private static IReadOnlyList<string> NormalizeTags(
        IReadOnlyList<string>? tags) =>
        tags?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TagNormalizer.Normalize(value).NormalizedKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray()
        ?? [];

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static int NormalizePageSize(int pageSize) =>
        pageSize is < 1 or > 100
            ? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "pageSize must be between 1 and 100.")
            : pageSize;

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

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrEmpty(safe) ? "knowledge" : safe;
    }

    private sealed record SummaryRow(
        Guid KnowledgeId,
        string Title,
        string Summary,
        Guid KnowledgeTypeId,
        string KnowledgeTypeKey,
        KnowledgeStatus Status,
        int Revision,
        DateTimeOffset UpdatedAt,
        string UpdatedAtText);

    private sealed record SearchRow(
        Guid KnowledgeId,
        string Title,
        string Summary,
        Guid KnowledgeTypeId,
        string KnowledgeTypeKey,
        KnowledgeStatus Status,
        int Revision,
        Guid RevisionId,
        DateTimeOffset UpdatedAt,
        string UpdatedAtText,
        double Rank);

    private sealed record KnowledgeTimeCursor(string UpdatedAt, string Id);
    private sealed record RevisionCursor(int Revision);
    private sealed record RelationCursor(string UpdatedAt, string Id);
    private sealed record SearchCursor(
        string UpdatedAt,
        string Id,
        double? Rank);
}
