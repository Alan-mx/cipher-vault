using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal static class FrontMatterTags
{
    public static bool TryExtract(
        string content,
        out IReadOnlyList<string> tags)
    {
        tags = [];
        if (!FrontMatterDocument.TryParse(content, out var snapshot))
        {
            return false;
        }

        var fields = snapshot.Fields
            .Where(field => string.Equals(
                field.Key,
                "tags",
                StringComparison.Ordinal))
            .ToArray();
        if (fields.Length == 0)
        {
            return false;
        }
        if (fields.Length > 1)
        {
            throw InvalidFrontMatter(
                "The frontmatter tags key is duplicated.");
        }

        var field = fields[0];
        if (field.Value.ValueKind == JsonValueKind.Array)
        {
            tags = field.Value.EnumerateArray()
                .Select(ReadTagElement)
                .ToArray();
            return true;
        }
        if (field.Value.ValueKind == JsonValueKind.String)
        {
            tags = (field.Value.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .ToArray();
            return true;
        }
        if (field.ValueType == "null")
        {
            return true;
        }
        if (field.Value.ValueKind is
            JsonValueKind.Number or
            JsonValueKind.True or
            JsonValueKind.False)
        {
            tags = [field.Value.GetRawText()];
            return true;
        }

        throw InvalidFrontMatter(
            "The frontmatter tags value must be a string or array.");
    }

    private static string ReadTagElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or
            JsonValueKind.True or
            JsonValueKind.False => element.GetRawText(),
            _ => throw InvalidFrontMatter(
                "Frontmatter tags array items must be scalar values.")
        };
    }

    private static VaultException InvalidFrontMatter(string message)
    {
        return new VaultException(
            VaultErrorCode.FrontMatterInvalid,
            message);
    }
}
