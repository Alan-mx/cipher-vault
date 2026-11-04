using System.Text.Json;
using System.Text.Json.Nodes;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Drafts;

internal static class DocumentDraftReviewContextCodec
{
    internal const int MaxSummaryScalars = 2_000;
    internal const int MaxSources = 8;
    internal const int MaxExcerptScalars = 1_000;
    internal const int MaxTitleScalars = 200;
    internal const int MaxPathOrReferenceScalars = 2_048;

    private static readonly HashSet<string> ContextKeys =
        new(StringComparer.Ordinal)
        {
            "summary",
            "sources"
        };

    private static readonly HashSet<string> SourceKeys =
        new(StringComparer.Ordinal)
        {
            "documentId",
            "title",
            "virtualPath",
            "reference",
            "excerpt"
        };

    private static readonly HashSet<string> TrashPayloadKeys =
        new(StringComparer.Ordinal)
        {
            "reviewContext"
        };

    internal static JsonElement MergeForUpdate(
        JsonElement incomingPayload,
        JsonElement existingPayload)
    {
        var root = ParseObject(incomingPayload, "Draft payload");
        var hasIncoming = incomingPayload.TryGetProperty(
            "reviewContext",
            out var incomingContext);
        if (hasIncoming)
        {
            if (incomingContext.ValueKind == JsonValueKind.Null)
            {
                _ = root.Remove("reviewContext");
            }
            else
            {
                ValidateContext(incomingContext);
                root["reviewContext"] = ToNode(incomingContext);
            }
        }
        else if (existingPayload.TryGetProperty(
                     "reviewContext",
                     out var existingContext)
                 && existingContext.ValueKind != JsonValueKind.Null)
        {
            ValidateContext(existingContext);
            root["reviewContext"] = ToNode(existingContext);
        }

        return Serialize(root);
    }

    internal static JsonElement? NormalizeForCreate(JsonElement payload)
    {
        if (!payload.TryGetProperty("reviewContext", out var context)
            || context.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        ValidateContext(context);
        return context.Clone();
    }

    internal static void ValidateFromPayload(JsonElement payload)
    {
        if (payload.TryGetProperty("reviewContext", out var context)
            && context.ValueKind != JsonValueKind.Null)
        {
            ValidateContext(context);
        }
    }

    internal static JsonElement NormalizeTrashPayload(JsonElement payload)
    {
        EnsureOnlyProperties(payload, "Trash draft payload", TrashPayloadKeys);
        var root = new JsonObject();
        if (payload.TryGetProperty("reviewContext", out var context)
            && context.ValueKind != JsonValueKind.Null)
        {
            ValidateContext(context);
            root["reviewContext"] = ToNode(context);
        }
        return Serialize(root);
    }

    internal static DocumentDraftReviewContext? ParseForDisplay(
        JsonElement payload,
        IReadOnlySet<Guid> availableDocumentIds)
    {
        if (!payload.TryGetProperty("reviewContext", out var context)
            || context.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var summary = ReadNullableString(
            context,
            "summary",
            MaxSummaryScalars);
        var sources = new List<DocumentDraftReviewSource>();
        if (context.TryGetProperty("sources", out var sourceArray)
            && sourceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sourceArray.EnumerateArray())
            {
                var documentId = ReadDocumentId(source);
                sources.Add(new DocumentDraftReviewSource(
                    documentId is { } id && availableDocumentIds.Contains(id)
                        ? id
                        : null,
                    ReadNullableString(source, "title", MaxTitleScalars),
                    ReadNullableString(
                        source,
                        "virtualPath",
                        MaxPathOrReferenceScalars),
                    ReadNullableString(
                        source,
                        "reference",
                        MaxPathOrReferenceScalars),
                    RequireString(
                        source,
                        "excerpt",
                        MaxExcerptScalars,
                        allowEmpty: false)));
            }
        }
        return new DocumentDraftReviewContext(summary, sources);
    }

    internal static bool HasContext(JsonElement payload) =>
        payload.TryGetProperty("reviewContext", out var context)
        && context.ValueKind == JsonValueKind.Object;

    internal static IReadOnlyList<Guid> ReadSourceDocumentIds(
        JsonElement payload)
    {
        if (!payload.TryGetProperty("reviewContext", out var context)
            || context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return sources
            .EnumerateArray()
            .Select(ReadDocumentId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
    }

    internal static void EnsureOnlyProperties(
        JsonElement element,
        string name,
        IReadOnlySet<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{name} must be a JSON object.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name))
            {
                throw Invalid($"{name} contains an unknown or duplicate field.");
            }
        }
    }

    private static void ValidateContext(JsonElement context)
    {
        EnsureOnlyProperties(context, "reviewContext", ContextKeys);
        _ = ReadNullableString(context, "summary", MaxSummaryScalars);
        if (!context.TryGetProperty("sources", out var sources))
        {
            return;
        }
        if (sources.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("reviewContext.sources must be an array.");
        }
        var count = 0;
        foreach (var source in sources.EnumerateArray())
        {
            if (++count > MaxSources)
            {
                throw Invalid(
                    $"reviewContext.sources supports at most {MaxSources} items.");
            }
            EnsureOnlyProperties(source, "reviewContext source", SourceKeys);
            _ = ReadDocumentId(source);
            _ = ReadNullableString(source, "title", MaxTitleScalars);
            _ = ReadNullableString(
                source,
                "virtualPath",
                MaxPathOrReferenceScalars);
            _ = ReadNullableString(
                source,
                "reference",
                MaxPathOrReferenceScalars);
            _ = RequireString(
                source,
                "excerpt",
                MaxExcerptScalars,
                allowEmpty: false);
        }
    }

    private static Guid? ReadDocumentId(JsonElement source)
    {
        if (!source.TryGetProperty("documentId", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var documentId)
            || documentId == Guid.Empty)
        {
            throw Invalid(
                "reviewContext source documentId must be a non-empty GUID.");
        }
        return documentId;
    }

    private static string? ReadNullableString(
        JsonElement element,
        string name,
        int maxScalars)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{name} must be a string or null.");
        }
        return ValidateScalars(value.GetString() ?? string.Empty, name, maxScalars);
    }

    private static string RequireString(
        JsonElement element,
        string name,
        int maxScalars,
        bool allowEmpty)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{name} must be a string.");
        }
        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && text.Length == 0)
        {
            throw Invalid($"{name} must not be empty.");
        }
        return ValidateScalars(text, name, maxScalars);
    }

    private static string ValidateScalars(
        string value,
        string name,
        int maxScalars)
    {
        int count;
        try
        {
            count = UnicodeText.CountScalars(value);
        }
        catch (ArgumentException)
        {
            throw Invalid($"{name} contains invalid Unicode data.");
        }
        if (count > maxScalars)
        {
            throw Invalid($"{name} exceeds the allowed character count.");
        }
        return value;
    }

    private static JsonObject ParseObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{name} must be a JSON object.");
        }
        return JsonNode.Parse(value.GetRawText()) as JsonObject
            ?? throw Invalid($"{name} must be a JSON object.");
    }

    private static JsonNode ToNode(JsonElement value) =>
        JsonNode.Parse(value.GetRawText())
        ?? throw Invalid("reviewContext could not be parsed.");

    private static JsonElement Serialize(JsonObject root) =>
        JsonSerializer.SerializeToElement(root);

    private static VaultException Invalid(string message) =>
        new(VaultErrorCode.DraftOperationInvalid, message);
}
