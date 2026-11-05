using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Knowledge;
using System.Globalization;

namespace CipherVault.Core.Storage.Knowledge;

public static class KnowledgeViewStore
{
    private const string CursorKind = "knowledge-view-documents";

    internal const string FacetSql =
        """
        WITH source AS (
            SELECT d.document_id,
                   CASE WHEN field.type = 'text'
                        THEN cv_knowledge_view_key(field.value)
                   END AS facet_key,
                   CASE WHEN field.type = 'text'
                        THEN cv_knowledge_view_label(field.value)
                   END AS label
            FROM documents AS d
            LEFT JOIN json_each(d.frontmatter_json) AS field
              ON field.key = $field
            WHERE d.deleted_at IS NULL
              AND ($activeOnly = 0 OR EXISTS (
                  SELECT 1
                  FROM json_each(d.frontmatter_json) AS status_field
                  WHERE status_field.key = 'status'
                    AND status_field.type = 'text'
                    AND status_field.value COLLATE BINARY = 'active'
              ))
        ),
        valid AS (
            SELECT document_id, facet_key, label
            FROM source
            WHERE facet_key IS NOT NULL
        ),
        label_counts AS (
            SELECT facet_key, label,
                   count(DISTINCT document_id) AS label_count
            FROM valid
            GROUP BY facet_key, label
        ),
        key_counts AS (
            SELECT facet_key,
                   count(DISTINCT document_id) AS document_count
            FROM valid
            GROUP BY facet_key
        ),
        label_ranked AS (
            SELECT label_counts.facet_key,
                   label_counts.label,
                   label_counts.label_count,
                   key_counts.document_count,
                   row_number() OVER (
                       PARTITION BY label_counts.facet_key
                       ORDER BY label_counts.label_count DESC,
                                cv_knowledge_view_ordinal_key(
                                    label_counts.label) ASC
                   ) AS label_rank
            FROM label_counts
            JOIN key_counts
              ON key_counts.facet_key = label_counts.facet_key
        ),
        ranked_facets AS (
            SELECT facet_key, label, document_count
            FROM label_ranked
            WHERE label_rank = 1
            ORDER BY document_count DESC,
                     cv_knowledge_view_ordinal_key(facet_key) ASC
            LIMIT 201
        ),
        unset AS (
            SELECT count(DISTINCT document_id) AS document_count
            FROM source
            WHERE facet_key IS NULL
        )
        SELECT is_unset, facet_key, label, document_count
        FROM (
            SELECT 0 AS is_unset, facet_key, label, document_count
            FROM ranked_facets
            UNION ALL
            SELECT 1 AS is_unset, NULL AS facet_key,
                   '未设置' AS label, document_count
            FROM unset
        )
        ORDER BY is_unset ASC,
                 document_count DESC,
                 CASE WHEN is_unset = 0
                      THEN cv_knowledge_view_ordinal_key(facet_key)
                 END ASC;
        """;

    private const string DocumentBaseSql =
        """
        SELECT d.document_id, d.title, d.path, d.format,
               d.revision, d.updated_at
        FROM documents AS d
        WHERE d.deleted_at IS NULL
          AND ($activeOnly = 0 OR EXISTS (
              SELECT 1
              FROM json_each(d.frontmatter_json) AS status_field
              WHERE status_field.key = 'status'
                AND status_field.type = 'text'
                AND status_field.value COLLATE BINARY = 'active'
          ))
          AND (
              ($unset = 1 AND NOT EXISTS (
                  SELECT 1
                  FROM json_each(d.frontmatter_json) AS field
                  WHERE field.key = $field
                    AND field.type = 'text'
                    AND cv_knowledge_view_key(field.value)
                        IS NOT NULL
              ))
              OR
              ($unset = 0 AND EXISTS (
                  SELECT 1
                  FROM json_each(d.frontmatter_json) AS field
                  WHERE field.key = $field
                    AND field.type = 'text'
                    AND cv_knowledge_view_key(field.value)
                        = $facetKey
              ))
          )
        """;

    private const string DocumentCursorSql =
        """

        AND (
            d.updated_at < $afterUpdated
            OR (
                d.updated_at = $afterUpdated
                AND d.document_id > $afterDocumentId
            )
        )
        """;

    private const string DocumentOrderSql =
        """

        ORDER BY d.updated_at DESC, d.document_id ASC
        LIMIT $limit;
        """;

    internal static string BuildDocumentSql(bool hasCursor) =>
        DocumentBaseSql
        + (hasCursor ? DocumentCursorSql : string.Empty)
        + DocumentOrderSql;

    public static async Task<KnowledgeFacetPage> GetFacetsAsync(
        VaultOperationContext context,
        KnowledgeViewField field,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var fieldName = ToDatabaseField(field);
        if (activeOnly && field != KnowledgeViewField.Project)
        {
            throw InvalidInput(
                "activeOnly is only supported for project views.");
        }

        using var read = context.CreateCommand();
        read.Command.CommandText = FacetSql;
        read.Command.Parameters.AddWithValue("$field", fieldName);
        read.Command.Parameters.AddWithValue(
            "$activeOnly",
            activeOnly ? 1L : 0L);

        var valid = new List<KnowledgeFacetItem>();
        KnowledgeFacetItem? unset = null;
        await using var reader = await read.Command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var isUnset = reader.GetInt64(0) == 1;
            var item = new KnowledgeFacetItem(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt32(reader.GetInt64(3)),
                isUnset);
            if (isUnset)
            {
                unset = item;
            }
            else
            {
                valid.Add(item);
            }
        }

        var truncated = valid.Count > 200;
        var items = valid.Take(200).ToList();
        items.Add(unset ?? new KnowledgeFacetItem(
            null,
            "未设置",
            0,
            true));
        return new KnowledgeFacetPage(field, items, truncated);
    }

    public static async Task<KnowledgeViewPage> GetDocumentsAsync(
        VaultOperationContext context,
        KnowledgeViewField field,
        string? facetKey,
        bool unset,
        bool activeOnly,
        CursorPageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(page);
        var fieldName = ToDatabaseField(field);
        if (activeOnly && field != KnowledgeViewField.Project)
        {
            throw InvalidInput(
                "activeOnly is only supported for project views.");
        }

        string? normalizedKey = null;
        if (unset)
        {
            if (!string.IsNullOrWhiteSpace(facetKey))
            {
                throw InvalidInput(
                    "facetKey and unset cannot be combined.");
            }
        }
        else
        {
            normalizedKey = KnowledgeViewKey.NormalizeKey(facetKey);
            if (normalizedKey is null)
            {
                throw InvalidInput("facetKey is required.");
            }
        }

        var pageSize = NormalizePageSize(page.PageSize);
        var fingerprint = CursorCodec.Fingerprint(
            CursorKind,
            field.ToString(),
            unset ? "<unset>" : normalizedKey,
            unset ? "true" : "false",
            activeOnly ? "true" : "false",
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            page.Cursor,
            CursorKind,
            fingerprint,
            out KnowledgeViewCursor? cursor);
        if (page.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Cursor is invalid for this knowledge view.");
        }

        // Keep the document page and its tag batch on one SQLite read snapshot.
        await context.BeginTransactionAsync(cancellationToken);

        var documents = new List<KnowledgeViewRow>();
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText = BuildDocumentSql(cursor is not null);
            read.Command.Parameters.AddWithValue("$field", fieldName);
            read.Command.Parameters.AddWithValue(
                "$facetKey",
                normalizedKey ?? string.Empty);
            read.Command.Parameters.AddWithValue(
                "$unset",
                unset ? 1L : 0L);
            read.Command.Parameters.AddWithValue(
                "$activeOnly",
                activeOnly ? 1L : 0L);
            read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
            if (cursor is not null)
            {
                read.Command.Parameters.AddWithValue(
                    "$afterUpdated",
                    cursor.UpdatedAt.ToUniversalTime().ToString("O"));
                read.Command.Parameters.AddWithValue(
                    "$afterDocumentId",
                    cursor.DocumentId.ToString("D"));
            }

            await using var reader = await read.Command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                documents.Add(new KnowledgeViewRow(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    VaultDocumentFormatExtensions.ParseDatabaseValue(
                        reader.GetString(3)),
                    reader.GetInt32(4),
                    ParseTimestamp(reader.GetString(5))));
            }
        }

        var hasMore = documents.Count > pageSize;
        if (hasMore)
        {
            documents.RemoveAt(documents.Count - 1);
        }

        var tags = await ReadTagsAsync(
            context,
            documents.Select(document => document.DocumentId),
            cancellationToken);
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                CursorKind,
                fingerprint,
                new KnowledgeViewCursor(
                    documents[^1].UpdatedAt,
                    documents[^1].DocumentId))
            : null;
        return new KnowledgeViewPage(
            documents
                .Select(document => new KnowledgeViewItem(
                    document.DocumentId,
                    document.Title,
                    document.VirtualPath,
                    document.Format,
                    document.Revision,
                    tags.GetValueOrDefault(document.DocumentId, []),
                    document.UpdatedAt))
                .ToArray(),
            nextCursor);
    }

    internal static async Task<Dictionary<Guid, IReadOnlyList<string>>>
        ReadTagsAsync(
            VaultOperationContext context,
            IEnumerable<Guid> documentIds,
            CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToArray();
        var result = ids.ToDictionary(
            id => id,
            _ => new List<string>());
        if (ids.Length == 0)
        {
            return [];
        }

        using var read = context.CreateCommand();
        var parameters = string.Join(
            ", ",
            ids.Select((_, index) => $"$id{index}"));
        read.Command.CommandText =
            $"""
             SELECT dt.document_id, t.name
             FROM document_tags AS dt
             JOIN tags AS t ON t.tag_id = dt.tag_id
             WHERE dt.document_id IN ({parameters})
             ORDER BY dt.document_id, t.normalized_key;
             """;
        for (var index = 0; index < ids.Length; index++)
        {
            read.Command.Parameters.AddWithValue(
                $"$id{index}",
                ids[index].ToString("D"));
        }

        await using var reader = await read.Command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[Guid.Parse(reader.GetString(0))].Add(reader.GetString(1));
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count == 0
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : pair.Value.ToArray());
    }

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 30;
        if (pageSize is < 1 or > 100)
        {
            throw InvalidInput(
                "Knowledge view page size must be between 1 and 100.");
        }
        return pageSize;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind)
        .ToUniversalTime();

    private static string ToDatabaseField(KnowledgeViewField field) =>
        field switch
        {
            KnowledgeViewField.Project => "project",
            KnowledgeViewField.Type => "type",
            KnowledgeViewField.Maturity => "maturity",
            _ => throw InvalidInput("Knowledge view field is invalid.")
        };

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);

    private sealed record KnowledgeViewRow(
        Guid DocumentId,
        string Title,
        string VirtualPath,
        VaultDocumentFormat Format,
        int Revision,
        DateTimeOffset UpdatedAt);
}
