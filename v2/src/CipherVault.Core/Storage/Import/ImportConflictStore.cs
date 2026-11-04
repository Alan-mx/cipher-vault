using CipherVault.Core.Database;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Storage.Import;

public static class ImportConflictStore
{
    public static async Task<ImportExistingDocuments> FindExistingAsync(
        VaultOperationContext context,
        IEnumerable<Guid> documentIds,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToArray();
        var activeById = new Dictionary<Guid, ImportExistingDocument>();
        var trashedIds = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(400))
        {
            using var read = context.CreateCommand();
            var parameters = string.Join(
                ", ",
                chunk.Select((_, index) => $"$id{index}"));
            read.Command.CommandText =
                $"""
                 SELECT document_id, path, format, content, deleted_at
                 FROM documents
                 WHERE document_id IN ({parameters});
                 """;
            for (var index = 0; index < chunk.Length; index++)
            {
                read.Command.Parameters.AddWithValue(
                    $"$id{index}",
                    chunk[index].ToString("D"));
            }
            await using var reader = await read.Command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                if (!reader.IsDBNull(4))
                {
                    trashedIds.Add(id);
                    continue;
                }
                var format = VaultDocumentFormatExtensions.ParseDatabaseValue(
                    reader.GetString(2));
                activeById[id] = new ImportExistingDocument(
                    id,
                    reader.GetString(1),
                    format,
                    DocumentContentFingerprint.Compute(
                        reader.GetString(3),
                        format));
            }
        }

        var activeByPath = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in paths.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            using var read = context.CreateCommand();
            var parameters = string.Join(
                ", ",
                chunk.Select((_, index) => $"$path{index}"));
            read.Command.CommandText =
                $"""
                 SELECT path
                 FROM documents
                 WHERE deleted_at IS NULL
                   AND path IN ({parameters});
                 """;
            for (var index = 0; index < chunk.Length; index++)
            {
                read.Command.Parameters.AddWithValue(
                    $"$path{index}",
                    chunk[index]);
            }
            await using var reader = await read.Command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                activeByPath.Add(reader.GetString(0));
            }
        }
        return new ImportExistingDocuments(
            activeById,
            trashedIds,
            activeByPath);
    }

    public static async Task<IReadOnlySet<string>> FindActivePathsAsync(
        VaultOperationContext context,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(paths);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in paths.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            using var read = context.CreateCommand();
            var parameters = string.Join(
                ", ",
                chunk.Select((_, index) => $"$path{index}"));
            read.Command.CommandText =
                $"""
                 SELECT path
                 FROM documents
                 WHERE deleted_at IS NULL
                   AND path IN ({parameters});
                 """;
            for (var index = 0; index < chunk.Length; index++)
            {
                read.Command.Parameters.AddWithValue(
                    $"$path{index}",
                    chunk[index]);
            }

            await using var reader = await read.Command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetString(0));
            }
        }
        return result;
    }

    public sealed record ImportExistingDocuments(
        IReadOnlyDictionary<Guid, ImportExistingDocument> ActiveById,
        IReadOnlySet<Guid> TrashedIds,
        IReadOnlySet<string> ActivePaths);

    public sealed record ImportExistingDocument(
        Guid DocumentId,
        string VirtualPath,
        VaultDocumentFormat Format,
        string ContentHash);
}
