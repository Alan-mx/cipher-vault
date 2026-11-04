using System.Globalization;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Search;

namespace CipherVault.Core.Storage.Semantic;

public static class SemanticSearchStore
{
    private const int MaxExcerptScalars = 500;

    public static async Task<SemanticSearchResult> SearchAsync(
        VaultOperationContext context,
        SemanticProfile profile,
        SearchQuery query,
        float[] normalizedQueryVector,
        int candidateLimit,
        DateTimeOffset snapshotAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(normalizedQueryVector);
        if (candidateLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        }
        if (normalizedQueryVector.Length != profile.Provider.Dimension)
        {
            throw new VaultException(
                CipherVault.Core.Errors.VaultErrorCode.InvalidInput,
                "Semantic query vector dimension does not match the active profile.");
        }

        var tags = TagNormalizer.NormalizeMany(query.Tags);
        var format = query.Format?.ToDatabaseValue();
        var properties = SearchPropertyFilterNormalizer.Normalize(
            query.PropertyFilters);
        using var read = context.CreateCommand();
        var sql = new StringBuilder(
            """
            SELECT
                c.document_id,
                c.source_revision,
                c.chunk_ordinal,
                c.heading_path_json,
                c.start_offset,
                c.end_offset,
                c.vector_blob,
                d.title,
                d.path,
                d.revision,
                d.content,
                d.updated_at
            FROM semantic_chunks AS c
            JOIN documents AS d ON d.document_id = c.document_id
            WHERE c.profile_id = $profileId
              AND d.deleted_at IS NULL
              AND d.revision = c.source_revision
              AND d.updated_at <= $snapshotAt
            """);
        var parameterIndex = 0;
        SearchStore.AppendCommonFilters(
            sql,
            read,
            query,
            tags,
            format,
            properties,
            query.PropertyMatch,
            ref parameterIndex);
        sql.Append(" ORDER BY c.document_id, c.chunk_ordinal;");
        read.Command.CommandText = sql.ToString();
        read.Command.Parameters.AddWithValue(
            "$profileId",
            profile.ProfileId);
        read.Command.Parameters.AddWithValue(
            "$snapshotAt",
            snapshotAt.ToUniversalTime().ToString("O"));

        var bestByDocument =
            new Dictionary<Guid, SemanticSearchHit>();
        var scannedDocuments = new HashSet<Guid>();
        await using var reader =
            await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var documentId = Guid.Parse(reader.GetString(0));
            scannedDocuments.Add(documentId);
            var vector = SemanticVectorCodec.Decode(
                (byte[])reader.GetValue(6),
                profile.Provider.Dimension);
            var score = SemanticVectorCodec.Dot(
                normalizedQueryVector,
                vector);
            if (bestByDocument.TryGetValue(documentId, out var current)
                && current.CosineScore >= score)
            {
                continue;
            }
            var content = reader.GetString(10);
            var startOffset = reader.GetInt32(4);
            var endOffset = reader.GetInt32(5);
            if (startOffset < 0
                || endOffset <= startOffset
                || endOffset > content.Length)
            {
                throw new VaultException(
                    CipherVault.Core.Errors.VaultErrorCode.CorruptVault,
                    "Semantic chunk offsets are invalid.");
            }
            bestByDocument[documentId] = new SemanticSearchHit(
                documentId,
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetInt32(1),
                reader.GetInt32(2),
                JsonSerializer.Deserialize<string[]>(
                    reader.GetString(3)) ?? [],
                startOffset,
                endOffset,
                Bound(content[startOffset..endOffset]),
                score,
                ParseTimestamp(reader.GetString(11)));
        }

        var ordered = bestByDocument.Values
            .OrderByDescending(hit => hit.CosineScore)
            .ThenByDescending(hit => hit.UpdatedAt)
            .ThenBy(hit => hit.DocumentId)
            .ToArray();
        return new SemanticSearchResult(
            ordered.Take(candidateLimit).ToArray(),
            scannedDocuments.Count,
            ordered.Length,
            ordered.Length > candidateLimit);
    }

    private static string Bound(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length <= MaxExcerptScalars)
        {
            return value;
        }
        var builder = new StringBuilder();
        foreach (var rune in runes.Take(MaxExcerptScalars))
        {
            builder.Append(rune);
        }
        return builder.ToString();
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
