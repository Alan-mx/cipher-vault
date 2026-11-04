using CipherVault.Core.Database;
using CipherVault.Core.Relations;

namespace CipherVault.Core.Storage.Relations;

public static class RelationDocumentStore
{
    public static async Task<IReadOnlyDictionary<Guid, RelationDocumentSnapshot>>
        ReadAsync(
            VaultOperationContext context,
            IReadOnlyCollection<Guid> documentIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documentIds);
        var ids = documentIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, RelationDocumentSnapshot>();
        }

        using var command = context.CreateCommand();
        var parameters = new List<string>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            var name = $"$document{index}";
            parameters.Add(name);
            command.Command.Parameters.AddWithValue(
                name,
                ids[index].ToString("D"));
        }
        command.Command.CommandText =
            $"""
             SELECT document_id, title, path, revision, deleted_at
             FROM documents
             WHERE document_id IN ({string.Join(", ", parameters)});
             """;
        var result = new Dictionary<Guid, RelationDocumentSnapshot>();
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            result[id] = new RelationDocumentSnapshot(
                id,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                !reader.IsDBNull(4));
        }
        return result;
    }
}
