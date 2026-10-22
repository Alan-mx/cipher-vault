using System.Text.Json;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Mcp;

public sealed class BridgeToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record BridgeMappedToolCall(
    string AdminMethod,
    JsonElement AdminPayload);

public static class BridgeToolMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    public static BridgeMappedToolCall Map(
        BridgeToolDefinition definition,
        JsonElement arguments)
    {
        if (definition.AdminMethod is null)
        {
            throw new BridgeToolException(
                "unsupported_tool",
                $"{definition.ToolName} cannot be mapped to an Admin method.");
        }
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeToolException(
                "invalid_input",
                "Tool arguments must be a JSON object.");
        }

        return definition.ToolName switch
        {
            "list_catalog" => Map(
                definition,
                new AdminCatalogListRequest(
                    OptionalString(arguments, "folder"),
                    OptionalString(arguments, "cursor"),
                    OptionalInt(arguments, "limit", 50))),
            "search_notes" => Map(
                definition,
                new AdminSearchNotesRequest(
                    RequiredString(arguments, "query"),
                    OptionalString(arguments, "folder"),
                    OptionalString(arguments, "cursor"),
                    OptionalInt(arguments, "limit", 10))),
            "read_note" => Map(
                definition,
                new AdminReadNoteRequest(
                    RequiredGuid(arguments, "documentId"),
                    OptionalInt(arguments, "offset", 0),
                    OptionalInt(arguments, "maxChars", 8_000))),
            "scan_notes" => Map(
                definition,
                new AdminScanNotesRequest(
                    OptionalString(arguments, "cursor"),
                    OptionalInt(arguments, "limit", 50))),
            "create_note" => Map(
                definition,
                new HostCreateNoteRequest(
                    RequiredString(arguments, "title"),
                    OptionalString(arguments, "folder"),
                    OptionalString(arguments, "format") ?? "md",
                    RequiredString(arguments, "content"),
                    OptionalStringArray(arguments, "tags"))),
            "append_note" => Map(
                definition,
                new AdminAppendNoteRequest(
                    RequiredGuid(arguments, "documentId"),
                    new HostAppendNoteRequest(
                        OptionalInt(arguments, "expectedRevision", 0),
                        RequiredString(arguments, "content")))),
            "update_note" => Map(
                definition,
                new AdminUpdateNoteRequest(
                    RequiredGuid(arguments, "documentId"),
                    new HostUpdateNoteRequest(
                        OptionalInt(arguments, "expectedRevision", 0),
                        OptionalString(arguments, "title"),
                        OptionalString(arguments, "folder"),
                        OptionalString(arguments, "content"),
                        OptionalStringArray(arguments, "tags")))),
            "delete_note" => Map(
                definition,
                new AdminTrashNoteRequest(
                    RequiredGuid(arguments, "documentId"),
                    new HostTrashNoteRequest(
                        OptionalInt(arguments, "expectedRevision", 0)))),
            _ => throw new BridgeToolException(
                "unsupported_tool",
                $"{definition.ToolName} cannot be mapped to an Admin method.")
        };
    }

    private static BridgeMappedToolCall Map<T>(
        BridgeToolDefinition definition,
        T request)
    {
        return new BridgeMappedToolCall(
            definition.AdminMethod!,
            JsonSerializer.SerializeToElement(request, JsonOptions));
    }

    private static string? OptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidProperty(name);
        }
        return value.GetString();
    }

    private static string RequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw InvalidProperty(name);
        }
        return value.GetString() ?? throw InvalidProperty(name);
    }

    private static int OptionalInt(
        JsonElement args,
        string name,
        int fallback)
    {
        if (!args.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }
        if (!value.TryGetInt32(out var result))
        {
            throw InvalidProperty(name);
        }
        return result;
    }

    private static Guid RequiredGuid(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out var result))
        {
            throw InvalidProperty(name);
        }
        return result;
    }

    private static IReadOnlyList<string>? OptionalStringArray(
        JsonElement args,
        string name)
    {
        if (!args.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String))
        {
            throw InvalidProperty(name);
        }
        return value.EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static BridgeToolException InvalidProperty(string name)
    {
        return new BridgeToolException(
            "invalid_input",
            $"The {name} argument is invalid.");
    }
}
