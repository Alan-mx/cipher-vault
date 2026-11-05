using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

internal static class KnowledgeBasePayloadParser
{
    internal static SourceMutationPayload ParseSource(
        JsonElement payload,
        KnowledgeModelSnapshot model)
    {
        EnsureOnly(
            payload,
            "sourceKind",
            "title",
            "metadata",
            "contentFormat",
            "contentText");
        var kind = ParseSourceKind(RequiredString(payload, "sourceKind"));
        var title = RequiredString(payload, "title").Trim();
        if (title.Length is < 1 or > 200)
        {
            throw Invalid("Source title must contain 1 to 200 characters.");
        }
        var metadata = RequiredObject(payload, "metadata");
        var format = ParseContentFormat(
            RequiredString(payload, "contentFormat"));
        var content = RequiredString(payload, "contentText");
        ValidateMetadata(model, kind, metadata);
        return new SourceMutationPayload(
            kind,
            title,
            metadata,
            format,
            content);
    }

    internal static KnowledgeMutationPayload ParseKnowledge(
        JsonElement payload,
        KnowledgeModelSnapshot model)
    {
        EnsureOnly(
            payload,
            "title",
            "summary",
            "knowledgeTypeId",
            "templateId",
            "contentMarkdown",
            "properties",
            "tags",
            "sourceReferences");
        var title = RequiredString(payload, "title").Trim();
        if (title.Length is < 1 or > 200)
        {
            throw Invalid("Knowledge title must contain 1 to 200 characters.");
        }
        var summary = OptionalString(payload, "summary") ?? string.Empty;
        if (summary.Length > 4000)
        {
            throw Invalid("Knowledge summary is too long.");
        }
        var typeId = RequiredGuid(payload, "knowledgeTypeId");
        var type = model.KnowledgeTypes.FirstOrDefault(item => item.TypeId == typeId)
            ?? throw Invalid("Knowledge type does not exist.");
        if (!type.IsEnabled)
        {
            throw Invalid("Knowledge type is disabled.");
        }
        var templateId = OptionalGuid(payload, "templateId");
        if (templateId is { } id
            && !model.Templates.Any(item =>
                item.TemplateId == id
                && item.KnowledgeTypeId == typeId
                && item.IsEnabled))
        {
            throw Invalid("Knowledge template is invalid.");
        }
        var content = RequiredString(payload, "contentMarkdown");
        var properties = RequiredObject(payload, "properties");
        ValidateProperties(type, properties);
        var tags = OptionalStringArray(payload, "tags");
        if (tags.Count > 50)
        {
            throw Invalid("Too many tags.");
        }
        var references = OptionalArray(payload, "sourceReferences")
            .Select(ParseSourceReference)
            .ToArray();
        return new KnowledgeMutationPayload(
            title,
            summary,
            typeId,
            templateId,
            RemoveFrontMatter(content),
            properties,
            tags,
            references);
    }

    internal static RelationMutationPayload ParseRelation(
        JsonElement payload,
        KnowledgeModelSnapshot model)
    {
        EnsureOnly(
            payload,
            "relationTypeId",
            "sourceKnowledgeId",
            "targetKnowledgeId",
            "sourceKnowledgeRevision",
            "targetKnowledgeRevision",
            "rationale",
            "evidence");
        var relationTypeId = RequiredGuid(payload, "relationTypeId");
        if (!model.RelationTypes.Any(item =>
                item.RelationTypeId == relationTypeId && item.IsEnabled))
        {
            throw Invalid("Relation type is invalid.");
        }
        var sourceId = RequiredGuid(payload, "sourceKnowledgeId");
        var targetId = RequiredGuid(payload, "targetKnowledgeId");
        if (sourceId == targetId)
        {
            throw Invalid("Relation endpoints must be different.");
        }
        var sourceRevision = RequiredInt(
            payload,
            "sourceKnowledgeRevision");
        var targetRevision = RequiredInt(
            payload,
            "targetKnowledgeRevision");
        if (sourceRevision < 1 || targetRevision < 1)
        {
            throw Invalid("Relation revision must be positive.");
        }
        var rationale = RequiredString(payload, "rationale").Trim();
        if (rationale.Length is < 1 or > 4000)
        {
            throw Invalid("Relation rationale is invalid.");
        }
        var evidence = OptionalArray(payload, "evidence");
        return new RelationMutationPayload(
            relationTypeId,
            sourceId,
            targetId,
            sourceRevision,
            targetRevision,
            rationale,
            JsonSerializer.SerializeToElement(evidence));
    }

    internal static void RequireEmptyPayload(JsonElement payload)
    {
        EnsureOnly(payload);
    }

    internal static IReadOnlyList<SourceReferenceView> PreviewSourceReferences(
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("sourceReferences", out var refs)
            || refs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return refs.EnumerateArray()
            .Select(reference =>
            {
                var item = ParseSourceReference(reference);
                return new SourceReferenceView(
                    Guid.Empty,
                    item.SourceId,
                    item.SourceRevision,
                    item.LocatorType,
                    item.LocatorValue,
                    item.Excerpt,
                    item.ExtractionMetadata);
            })
            .ToArray();
    }

    private static SourceReferenceInput ParseSourceReference(
        JsonElement element)
    {
        EnsureOnly(
            element,
            "sourceId",
            "sourceRevision",
            "locatorType",
            "locatorValue",
            "excerpt",
            "extractionMetadata");
        var sourceId = RequiredGuid(element, "sourceId");
        var revision = RequiredInt(element, "sourceRevision");
        if (revision < 1)
        {
            throw Invalid("Source revision must be positive.");
        }
        var type = ParseLocatorType(RequiredString(element, "locatorType"));
        var value = RequiredElement(element, "locatorValue");
        var excerpt = RequiredString(element, "excerpt");
        if (excerpt.Length is < 1 or > 4000)
        {
            throw Invalid("Source excerpt is invalid.");
        }
        var metadata = OptionalObject(element, "extractionMetadata")
            ?? JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>());
        var canonicalValue = CanonicalLocatorValue(type, value);
        return new SourceReferenceInput(
            sourceId,
            revision,
            type,
            canonicalValue,
            excerpt,
            metadata);
    }

    private static JsonElement CanonicalLocatorValue(
        SourceLocatorType type,
        JsonElement value)
    {
        if (type == SourceLocatorType.Section)
        {
            if (value.ValueKind != JsonValueKind.Array
                || value.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.String))
            {
                throw Invalid("Section locator must be a string array.");
            }
            return value.Clone();
        }
        if (type == SourceLocatorType.Paragraph)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("sectionPath", out var path)
                || path.ValueKind != JsonValueKind.Array
                || !value.TryGetProperty("ordinal", out var ordinal)
                || ordinal.ValueKind != JsonValueKind.Number)
            {
                throw Invalid("Paragraph locator is invalid.");
            }
            return JsonSerializer.SerializeToElement(new
            {
                sectionPath = path.EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToArray(),
                ordinal = ordinal.GetInt32()
            });
        }
        return value.Clone();
    }

    private static void ValidateProperties(
        KnowledgeTypeDefinition type,
        JsonElement properties)
    {
        var definitions = type.Properties
            .Where(item => item.IsEnabled)
            .ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            if (!definitions.ContainsKey(property.Name))
            {
                throw Invalid($"Property {property.Name} is not defined.");
            }
        }
        foreach (var definition in definitions.Values)
        {
            if (definition.Required
                && (!properties.TryGetProperty(
                        definition.Key,
                        out var value)
                    || value.ValueKind == JsonValueKind.Null))
            {
                throw Invalid($"Property {definition.Key} is required.");
            }
            if (properties.TryGetProperty(definition.Key, out var actual))
            {
                ValidatePropertyValue(definition, actual);
            }
        }
    }

    private static void ValidatePropertyValue(
        KnowledgePropertyDefinition definition,
        JsonElement value)
    {
        var valid = definition.ValueType switch
        {
            "string" or "date" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "enum" => value.ValueKind == JsonValueKind.String,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false
        };
        if (!valid)
        {
            throw Invalid($"Property {definition.Key} has an invalid type.");
        }
    }

    private static void ValidateMetadata(
        KnowledgeModelSnapshot model,
        SourceKind kind,
        JsonElement metadata)
    {
        var rule = model.SourceRules.FirstOrDefault(item =>
            item.Kind == kind && item.IsEnabled)
            ?? throw new VaultException(
                VaultErrorCode.SourceMetadataInvalid,
                "Source rule is unavailable.");
        if (!rule.MetadataSchema.TryGetProperty(
                "required",
                out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var name in required.EnumerateArray()
                     .Select(item => item.GetString())
                     .Where(item => item is not null))
        {
            if (!metadata.TryGetProperty(name!, out var value)
                || value.ValueKind is JsonValueKind.Null
                    or JsonValueKind.Undefined)
            {
                throw new VaultException(
                    VaultErrorCode.SourceMetadataInvalid,
                    $"Source metadata {name} is required.");
            }
        }
    }

    private static string RemoveFrontMatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return content;
        }
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return content;
        }
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterModelMismatch,
                "Frontmatter is not closed.");
        }
        var bodyStart = normalized.IndexOf('\n', end + 4);
        return bodyStart < 0 ? string.Empty : normalized[(bodyStart + 1)..];
    }

    private static SourceKind ParseSourceKind(string value) =>
        value switch
        {
            "web_page" => SourceKind.WebPage,
            "local_file" => SourceKind.LocalFile,
            "book" => SourceKind.Book,
            "podcast" => SourceKind.Podcast,
            "other" => SourceKind.Other,
            _ => throw Invalid("SourceKind is invalid.")
        };

    private static SourceContentFormat ParseContentFormat(string value) =>
        value switch
        {
            "markdown" => SourceContentFormat.Markdown,
            "text" => SourceContentFormat.Text,
            _ => throw Invalid("Content format is invalid.")
        };

    private static SourceLocatorType ParseLocatorType(string value) =>
        value switch
        {
            "section" => SourceLocatorType.Section,
            "paragraph" => SourceLocatorType.Paragraph,
            "page" => SourceLocatorType.Page,
            "timestamp" => SourceLocatorType.Timestamp,
            _ => throw Invalid("Locator type is invalid.")
        };

    private static void EnsureOnly(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Payload must be a JSON object.");
        }
        var allowed = keys.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name))
            {
                throw Invalid("Payload contains an unknown or duplicate field.");
            }
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
            ? text
            : throw Invalid($"{name} is required.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw Invalid($"{name} must be a string.")
            : null;

    private static Guid RequiredGuid(JsonElement element, string name) =>
        OptionalGuid(element, name)
        ?? throw Invalid($"{name} is required.");

    private static Guid? OptionalGuid(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var id)
            && id != Guid.Empty
                ? id
                : throw Invalid($"{name} must be a UUID.");
    }

    private static int RequiredInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : throw Invalid($"{name} must be an integer.");

    private static JsonElement RequiredObject(JsonElement element, string name) =>
        OptionalObject(element, name)
        ?? throw Invalid($"{name} is required.");

    private static JsonElement? OptionalObject(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Object
            ? value.Clone()
            : throw Invalid($"{name} must be an object.");
    }

    private static JsonElement RequiredElement(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.Clone()
            : throw Invalid($"{name} is required.");

    private static IReadOnlyList<string> OptionalStringArray(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String))
        {
            throw Invalid($"{name} must be a string array.");
        }
        return value.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    private static IReadOnlyList<JsonElement> OptionalArray(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.Clone()).ToArray()
            : throw Invalid($"{name} must be an array.");
    }

    private static VaultException Invalid(string message) =>
        new(VaultErrorCode.DraftOperationInvalid, message);
}
