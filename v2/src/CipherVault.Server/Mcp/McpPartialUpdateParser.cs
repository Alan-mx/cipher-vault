using System.Text.Json;
using CipherVault.Application.Documents;

namespace CipherVault.Server.Mcp;

public static class McpPartialUpdateParser
{
    private static readonly HashSet<string> AllowedProperties =
    [
        "title",
        "virtualPath",
        "content",
        "tags"
    ];

    public static DocumentPartialUpdate Parse(JsonElement changes)
    {
        if (changes.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("changes must be an object.");
        }

        var properties = changes.EnumerateObject().ToArray();
        if (properties.Length == 0)
        {
            throw Invalid("At least one update field is required.");
        }
        if (properties.Any(property => !AllowedProperties.Contains(property.Name)))
        {
            throw Invalid("changes contains an unknown field.");
        }
        if (properties.Any(property => property.Value.ValueKind == JsonValueKind.Null))
        {
            throw Invalid("Update fields cannot be null.");
        }

        var title = ReadString(changes, "title");
        var virtualPath = ReadString(changes, "virtualPath");
        var content = ReadString(changes, "content");
        var tags = ReadTags(changes);
        return new DocumentPartialUpdate(
            title,
            virtualPath,
            content,
            tags);
    }

    private static OptionalField<string> ReadString(
        JsonElement changes,
        string name)
    {
        if (!changes.TryGetProperty(name, out var value))
        {
            return OptionalField<string>.Missing;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{name} must be a string.");
        }
        return OptionalField<string>.Of(value.GetString()!);
    }

    private static OptionalField<IReadOnlyList<string>> ReadTags(
        JsonElement changes)
    {
        if (!changes.TryGetProperty("tags", out var value))
        {
            return OptionalField<IReadOnlyList<string>>.Missing;
        }
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String))
        {
            throw Invalid("tags must be an array of strings.");
        }

        return OptionalField<IReadOnlyList<string>>.Of(
            value.EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    private static McpToolRequestException Invalid(string message) =>
        new("invalid_input", message);
}
