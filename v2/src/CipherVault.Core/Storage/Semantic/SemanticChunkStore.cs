using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Semantic;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Semantic;

public static class SemanticChunkStore
{
    public static async Task ReplaceDocumentAsync(
        VaultOperationContext context,
        Guid documentId,
        string profileId,
        int sourceRevision,
        int dimension,
        IReadOnlyList<SemanticChunkRecord> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            if (chunk.DocumentId != documentId
                || !string.Equals(
                    chunk.ProfileId,
                    profileId,
                    StringComparison.Ordinal)
                || chunk.SourceRevision != sourceRevision
                || chunk.Dimension != dimension)
            {
                throw new ArgumentException(
                    "Semantic chunks must share document, profile, revision and dimension.",
                    nameof(chunks));
            }
        }

        await DeleteDocumentAsync(
            context,
            documentId,
            profileId,
            cancellationToken);
        foreach (var chunk in chunks)
        {
            using var command = context.CreateCommand();
            command.Command.CommandText =
                """
                INSERT INTO semantic_chunks(
                    document_id,
                    profile_id,
                    source_revision,
                    chunk_ordinal,
                    heading_path_json,
                    start_offset,
                    end_offset,
                    chunk_content_hash,
                    dimension,
                    vector_blob,
                    created_at)
                VALUES(
                    $documentId,
                    $profileId,
                    $sourceRevision,
                    $chunkOrdinal,
                    $headingPathJson,
                    $startOffset,
                    $endOffset,
                    $chunkContentHash,
                    $dimension,
                    $vectorBlob,
                    $createdAt);
                """;
            command.Command.Parameters.AddWithValue(
                "$documentId",
                chunk.DocumentId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$profileId",
                chunk.ProfileId);
            command.Command.Parameters.AddWithValue(
                "$sourceRevision",
                chunk.SourceRevision);
            command.Command.Parameters.AddWithValue(
                "$chunkOrdinal",
                chunk.ChunkOrdinal);
            command.Command.Parameters.AddWithValue(
                "$headingPathJson",
                JsonSerializer.Serialize(chunk.HeadingPath));
            command.Command.Parameters.AddWithValue(
                "$startOffset",
                chunk.StartOffset);
            command.Command.Parameters.AddWithValue(
                "$endOffset",
                chunk.EndOffset);
            command.Command.Parameters.AddWithValue(
                "$chunkContentHash",
                chunk.ContentHash);
            command.Command.Parameters.AddWithValue(
                "$dimension",
                chunk.Dimension);
            command.Command.Parameters.AddWithValue(
                "$vectorBlob",
                chunk.VectorBlob);
            command.Command.Parameters.AddWithValue(
                "$createdAt",
                chunk.CreatedAt.ToUniversalTime().ToString("O"));
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task<int> DeleteDocumentAsync(
        VaultOperationContext context,
        Guid documentId,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            DELETE FROM semantic_chunks
            WHERE document_id = $documentId
              AND profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> DeleteProfileAsync(
        VaultOperationContext context,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            "DELETE FROM semantic_chunks WHERE profile_id = $profileId;";
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> CountAsync(
        VaultOperationContext context,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT count(*)
            FROM semantic_chunks
            WHERE profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public static async Task<int> CountCurrentDocumentsAsync(
        VaultOperationContext context,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT count(DISTINCT c.document_id)
            FROM semantic_chunks AS c
            JOIN documents AS d ON d.document_id = c.document_id
            WHERE c.profile_id = $profileId
              AND d.deleted_at IS NULL
              AND d.revision = c.source_revision;
            """;
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    internal static async Task<IReadOnlyList<SemanticChunkRecord>> ReadDocumentAsync(
        VaultOperationContext context,
        Guid documentId,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                source_revision,
                chunk_ordinal,
                heading_path_json,
                start_offset,
                end_offset,
                chunk_content_hash,
                dimension,
                vector_blob,
                created_at
            FROM semantic_chunks
            WHERE document_id = $documentId
              AND profile_id = $profileId
            ORDER BY chunk_ordinal;
            """;
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        var result = new List<SemanticChunkRecord>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadChunk(reader, documentId, profileId));
        }
        return result;
    }

    private static SemanticChunkRecord ReadChunk(
        SqliteDataReader reader,
        Guid documentId,
        string profileId) =>
        new(
            documentId,
            profileId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            JsonSerializer.Deserialize<string[]>(
                reader.GetString(2)) ?? [],
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetInt32(6),
            (byte[])reader.GetValue(7),
            DateTimeOffset.Parse(
                reader.GetString(8),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).ToUniversalTime());
}
