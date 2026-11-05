using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Knowledge;

namespace CipherVault.Core.Storage.Knowledge;

public static class KnowledgeMaturityQueueStore
{
    private const string CursorKind = "knowledge-maturity-queue";
    private const string QueryVersion = "1";
    internal const string QueueSql =
        """
        SELECT
            d.document_id,
            d.title,
            d.path,
            d.format,
            d.revision,
            d.updated_at,
            CASE WHEN field.type = 'text'
                 THEN field.value
            END AS maturity_value,
            field.type AS maturity_json_type
        FROM documents AS d
        LEFT JOIN json_each(d.frontmatter_json) AS field
          ON field.key = 'maturity'
        WHERE d.deleted_at IS NULL
          AND cv_knowledge_maturity_bucket(
              field.type,
              field.value
          ) = $bucket
          AND (
              $afterUpdatedAt IS NULL
              OR d.updated_at < $afterUpdatedAt
              OR (
                  d.updated_at = $afterUpdatedAt
                  AND d.document_id > $afterDocumentId
              )
          )
        ORDER BY d.updated_at DESC, d.document_id ASC
        LIMIT $limit;
        """;

    public static async Task<KnowledgeMaturityQueuePage> ListAsync(
        VaultOperationContext context,
        KnowledgeMaturityQueueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Page);
        if (!Enum.IsDefined(query.Bucket))
        {
            throw InvalidInput("Maturity queue bucket is invalid.");
        }

        var pageSize = NormalizePageSize(query.Page.PageSize);
        var bucket = KnowledgeMaturity.ToDatabaseValue(query.Bucket);
        var fingerprint = CursorCodec.Fingerprint(
            CursorKind,
            QueryVersion,
            bucket,
            pageSize.ToString(CultureInfo.InvariantCulture));
        var hasCursor = CursorCodec.TryDecode(
            query.Page.Cursor,
            CursorKind,
            fingerprint,
            out KnowledgeMaturityQueueCursor? cursor);
        if (query.Page.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Cursor is invalid for this maturity queue.");
        }

        await context.BeginTransactionAsync(cancellationToken);
        var rows = new List<QueueRow>();
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText = QueueSql;
            read.Command.Parameters.AddWithValue("$bucket", bucket);
            read.Command.Parameters.AddWithValue(
                "$afterUpdatedAt",
                cursor?.UpdatedAt.ToUniversalTime().ToString("O")
                ?? (object)DBNull.Value);
            read.Command.Parameters.AddWithValue(
                "$afterDocumentId",
                cursor?.DocumentId.ToString("D")
                ?? (object)DBNull.Value);
            read.Command.Parameters.AddWithValue("$limit", pageSize + 1);
            await using var reader =
                await read.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new QueueRow(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    VaultDocumentFormatExtensions.ParseDatabaseValue(
                        reader.GetString(3)),
                    reader.GetInt32(4),
                    ParseTimestamp(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var tags = await KnowledgeViewStore.ReadTagsAsync(
            context,
            rows.Select(row => row.DocumentId),
            cancellationToken);
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                CursorKind,
                fingerprint,
                new KnowledgeMaturityQueueCursor(
                    rows[^1].UpdatedAt,
                    rows[^1].DocumentId))
            : null;
        return new KnowledgeMaturityQueuePage(
            rows.Select(row => new KnowledgeMaturityQueueItem(
                row.DocumentId,
                row.Title,
                row.VirtualPath,
                row.Format,
                row.Revision,
                tags.GetValueOrDefault(row.DocumentId, []),
                row.UpdatedAt,
                row.MaturityValue,
                KnowledgeMaturity.ParseValueType(row.JsonType),
                query.Bucket,
                KnowledgeMaturity.Next(
                    row.JsonType,
                    row.MaturityValue))).ToArray(),
            nextCursor);
    }

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 30;
        if (pageSize is < 1 or > 100)
        {
            throw InvalidInput(
                "Maturity queue page size must be between 1 and 100.");
        }
        return pageSize;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);

    private sealed record QueueRow(
        Guid DocumentId,
        string Title,
        string VirtualPath,
        VaultDocumentFormat Format,
        int Revision,
        DateTimeOffset UpdatedAt,
        string? MaturityValue,
        string? JsonType);

    private sealed record KnowledgeMaturityQueueCursor(
        DateTimeOffset UpdatedAt,
        Guid DocumentId);
}
