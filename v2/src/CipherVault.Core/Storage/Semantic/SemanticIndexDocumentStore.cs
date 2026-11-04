using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Semantic;

namespace CipherVault.Core.Storage.Semantic;

public static class SemanticIndexDocumentStore
{
    public static async Task<SemanticDocumentSnapshot?> ReadCurrentAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        string title;
        string format;
        string content;
        string frontMatterJson;
        int revision;
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                SELECT
                    title,
                    format,
                    content,
                    frontmatter_json,
                    revision
                FROM documents
                WHERE document_id = $documentId
                  AND deleted_at IS NULL;
                """;
            command.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader =
                await command.Command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            title = reader.GetString(0);
            format = reader.GetString(1);
            content = reader.GetString(2);
            frontMatterJson = reader.GetString(3);
            revision = reader.GetInt32(4);
        }

        var tags = new List<string>();
        using (var tagCommand = context.CreateCommand())
        {
            tagCommand.Command.CommandText =
                """
                SELECT t.name
                FROM document_tags AS dt
                JOIN tags AS t ON t.tag_id = dt.tag_id
                WHERE dt.document_id = $documentId
                ORDER BY t.normalized_key;
                """;
            tagCommand.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader =
                await tagCommand.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tags.Add(reader.GetString(0));
            }
        }

        var contextFields = ReadContextFields(frontMatterJson);
        return new SemanticDocumentSnapshot(
            documentId,
            revision,
            title,
            VaultDocumentFormatExtensions.ParseDatabaseValue(format),
            content,
            tags,
            contextFields,
            SemanticProfileIdentity.Sha256Hex(content));
    }

    private static IReadOnlyDictionary<string, string> ReadContextFields(
        string frontMatterJson)
    {
        using var document = JsonDocument.Parse(frontMatterJson);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "project", "type", "maturity" })
        {
            if (document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                result[key] = value.GetString() ?? string.Empty;
            }
        }
        return result;
    }
}
