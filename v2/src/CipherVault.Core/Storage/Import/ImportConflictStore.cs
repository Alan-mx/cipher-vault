using CipherVault.Core.Database;

namespace CipherVault.Core.Storage.Import;

public static class ImportConflictStore
{
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
}
