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
            throw InvalidAt(
                "title",
                "Source title must contain 1 to 200 characters.");
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
            throw InvalidAt(
                "title",
                "Knowledge title must contain 1 to 200 characters.");
        }
        var summary = OptionalString(payload, "summary") ?? string.Empty;
        if (summary.Length > 4000)
        {
            throw InvalidAt("summary", "Knowledge summary is too long.");
        }
        var typeId = RequiredGuid(payload, "knowledgeTypeId");
        var type = model.KnowledgeTypes.FirstOrDefault(item => item.TypeId == typeId)
            ?? throw InvalidAt(
                "knowledgeTypeId",
                "Knowledge type does not exist.");
        if (!type.IsEnabled)
        {
            throw InvalidAt(
                "knowledgeTypeId",
                "Knowledge type is disabled.");
        }
        var templateId = OptionalGuid(payload, "templateId");
        if (templateId is { } id
            && !model.Templates.Any(item =>
                item.TemplateId == id
                && item.KnowledgeTypeId == typeId
                && item.IsEnabled))
        {
            throw InvalidAt(
                "templateId",
                "Knowledge template is invalid.");
        }
        var content = RequiredString(payload, "contentMarkdown");
        var properties = RequiredObject(payload, "properties");
        ValidateProperties(type, properties);
        var tags = OptionalStringArray(payload, "tags");
        if (tags.Count > 50)
        {
            throw InvalidAt("tags", "Too many tags.");
        }
        var contentBody = RemoveAndValidateFrontMatter(
            content,
            type,
            properties,
            tags);
        var references = OptionalArray(payload, "sourceReferences")
            .Select(ParseSourceReference)
            .ToArray();
        return new KnowledgeMutationPayload(
            title,
            summary,
            typeId,
            templateId,
            contentBody,
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
            throw InvalidAt(
                "relationTypeId",
                "Relation type is invalid.");
        }
        var sourceId = RequiredGuid(payload, "sourceKnowledgeId");
        var targetId = RequiredGuid(payload, "targetKnowledgeId");
        if (sourceId == targetId)
        {
            throw InvalidAt(
                "targetKnowledgeId",
                "Relation endpoints must be different.");
        }
        var sourceRevision = RequiredInt(
            payload,
            "sourceKnowledgeRevision");
        var targetRevision = RequiredInt(
            payload,
            "targetKnowledgeRevision");
        if (sourceRevision < 1 || targetRevision < 1)
        {
            throw InvalidAt(
                sourceRevision < 1
                    ? "sourceKnowledgeRevision"
                    : "targetKnowledgeRevision",
                "Relation revision must be positive.");
        }
        var rationale = RequiredString(payload, "rationale").Trim();
        if (rationale.Length is < 1 or > 4000)
        {
            throw InvalidAt(
                "rationale",
                "Relation rationale is invalid.");
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
            throw InvalidAt(
                "sourceReferences",
                "Source revision must be positive.");
        }
        var type = ParseLocatorType(RequiredString(element, "locatorType"));
        var value = RequiredElement(element, "locatorValue");
        var excerpt = RequiredString(element, "excerpt");
        if (excerpt.Length is < 1 or > 4000)
        {
            throw InvalidAt(
                "sourceReferences",
                "Source excerpt is invalid.");
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
                throw InvalidAt(
                    $"properties.{property.Name}",
                    $"Property {property.Name} is not defined.");
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
                throw InvalidAt(
                    $"properties.{definition.Key}",
                    $"Property {definition.Key} is required.");
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
            throw InvalidAt(
                $"properties.{definition.Key}",
                $"Property {definition.Key} has an invalid type.");
        }
        ValidatePropertyConstraints(
            definition,
            value,
            $"properties.{definition.Key}");
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
                "properties",
                out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            properties = default;
        }
        var definitions = properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject()
                .ToDictionary(
                    item => item.Name,
                    item => item.Value,
                    StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (rule.MetadataSchema.TryGetProperty(
                "additionalProperties",
                out var additional)
            && additional.ValueKind == JsonValueKind.False)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!definitions.ContainsKey(property.Name))
                {
                    throw SourceMetadataInvalid(
                        $"metadata.{property.Name}",
                        $"Source metadata {property.Name} is not defined.");
                }
            }
        }
        foreach (var property in metadata.EnumerateObject())
        {
            if (definitions.TryGetValue(property.Name, out var schema))
            {
                ValidateMetadataValue(
                    property.Name,
                    schema,
                    property.Value,
                    $"metadata.{property.Name}");
            }
        }
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
                    $"Source metadata {name} is required.",
                    details: new Dictionary<string, object?>
                    {
                        ["fieldPath"] = $"metadata.{name}"
                    });
            }
        }
    }

    private static void ValidatePropertyConstraints(
        KnowledgePropertyDefinition definition,
        JsonElement value,
        string fieldPath)
    {
        var constraints = definition.Constraints;
        if (constraints.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (definition.ValueType == "enum"
            && constraints.TryGetProperty("options", out var options))
        {
            if (options.ValueKind != JsonValueKind.Array
                || options.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.String))
            {
                throw InvalidAt(
                    fieldPath,
                    "Enum options are invalid.");
            }
            var text = value.GetString() ?? string.Empty;
            if (!options.EnumerateArray().Any(item =>
                    string.Equals(
                        item.GetString(),
                        text,
                        StringComparison.Ordinal)))
            {
                throw InvalidAt(
                    fieldPath,
                    "Enum value is not allowed.");
            }
        }
        if (definition.ValueType is "string" or "date")
        {
            var length = (value.GetString() ?? string.Empty)
                .EnumerateRunes()
                .Count();
            ValidateRange(
                constraints,
                "minLength",
                "maxLength",
                length,
                fieldPath);
        }
        if (definition.ValueType == "number")
        {
            var number = value.GetDecimal();
            if (constraints.TryGetProperty("minimum", out var minimum)
                && number < minimum.GetDecimal())
            {
                throw InvalidAt(
                    fieldPath,
                    "Number is below the minimum.");
            }
            if (constraints.TryGetProperty("maximum", out var maximum)
                && number > maximum.GetDecimal())
            {
                throw InvalidAt(
                    fieldPath,
                    "Number exceeds the maximum.");
            }
            if (constraints.TryGetProperty("integer", out var integer)
                && integer.ValueKind == JsonValueKind.True
                && number != decimal.Truncate(number))
            {
                throw InvalidAt(
                    fieldPath,
                    "Number must be an integer.");
            }
        }
        if (definition.ValueType == "array")
        {
            ValidateRange(
                constraints,
                "minItems",
                "maxItems",
                value.GetArrayLength(),
                fieldPath);
            if (constraints.TryGetProperty("items", out var items))
            {
                foreach (var item in value.EnumerateArray())
                {
                    ValidateMetadataValue(
                        definition.Key,
                        items,
                        item,
                        fieldPath);
                }
            }
        }
        if (definition.ValueType == "object"
            && constraints.TryGetProperty("properties", out var properties))
        {
            ValidateMetadataValue(
                definition.Key,
                constraints,
                value,
                fieldPath);
        }
    }

    private static void ValidateMetadataValue(
        string name,
        JsonElement schema,
        JsonElement value,
        string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        var type = schema.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
        var valid = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "date" => value.ValueKind == JsonValueKind.String
                && (DateOnly.TryParse(
                        value.GetString(),
                        out _)
                    || DateTimeOffset.TryParse(
                        value.GetString(),
                        out _)),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is
                JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            null => true,
            _ => false
        };
        if (!valid)
        {
            throw SourceMetadataInvalid(
                fieldPath,
                $"{name} has an invalid type.");
        }
        if (value.ValueKind == JsonValueKind.Array
            && schema.TryGetProperty("itemType", out var itemType)
            && itemType.ValueKind == JsonValueKind.String)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateMetadataValue(
                    name,
                    JsonSerializer.SerializeToElement(
                        new Dictionary<string, JsonElement>
                        {
                            ["type"] = itemType
                        }),
                    item,
                    fieldPath);
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var definitions =
                schema.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                    ? properties.EnumerateObject().ToDictionary(
                        item => item.Name,
                        item => item.Value,
                        StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement>(
                        StringComparer.Ordinal);
            if (schema.TryGetProperty(
                    "additionalProperties",
                    out var additional)
                && additional.ValueKind == JsonValueKind.False)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (!definitions.ContainsKey(property.Name))
                    {
                        throw SourceMetadataInvalid(
                            $"{fieldPath}.{property.Name}",
                            $"{property.Name} is not allowed.");
                    }
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                if (definitions.TryGetValue(
                        property.Name,
                        out var propertySchema))
                {
                    ValidateMetadataValue(
                        property.Name,
                        propertySchema,
                        property.Value,
                        $"{fieldPath}.{property.Name}");
                }
            }
        }
    }

    private static void ValidateRange(
        JsonElement constraints,
        string minimumName,
        string maximumName,
        int actual,
        string fieldPath)
    {
        if (constraints.TryGetProperty(
                minimumName,
                out var minimum)
            && actual < minimum.GetInt32())
        {
            throw InvalidAt(
                fieldPath,
                $"Value is below {minimumName}.");
        }
        if (constraints.TryGetProperty(
                maximumName,
                out var maximum)
            && actual > maximum.GetInt32())
        {
            throw InvalidAt(
                fieldPath,
                $"Value exceeds {maximumName}.");
        }
    }

    private static string RemoveAndValidateFrontMatter(
        string content,
        KnowledgeTypeDefinition type,
        JsonElement properties,
        IReadOnlyList<string> tags)
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
        var lines = normalized.Split('\n');
        var closingIndex = Array.FindIndex(
            lines,
            1,
            line => string.Equals(
                line.Trim(),
                "---",
                StringComparison.Ordinal));
        if (closingIndex < 0)
        {
            throw new VaultException(
                VaultErrorCode.FrontMatterModelMismatch,
                "Frontmatter is not closed.");
        }
        var values = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        for (var index = 1; index < closingIndex; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw FrontMatterInvalid("$", "Frontmatter line is invalid.");
            }
            var key = line[..separator].Trim();
            var rawValue = line[(separator + 1)..].Trim();
            if (key.Length == 0 || !values.TryAdd(key, ParseScalar(rawValue)))
            {
                throw FrontMatterInvalid(
                    key,
                    "Frontmatter key is empty or duplicated.");
            }
        }

        if (!values.TryGetValue("type", out var typeValue)
            || typeValue.ValueKind != JsonValueKind.String
            || !string.Equals(
                typeValue.GetString(),
                type.Key,
                StringComparison.Ordinal))
        {
            throw FrontMatterInvalid(
                "type",
                "Frontmatter type conflicts with structured Knowledge.");
        }
        if (values.TryGetValue("tags", out var frontMatterTags))
        {
            if (frontMatterTags.ValueKind != JsonValueKind.Array
                || frontMatterTags.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.String))
            {
                throw FrontMatterInvalid(
                    "tags",
                    "Frontmatter tags must be a JSON string array.");
            }
            var frontKeys = TagNormalizer.NormalizeMany(
                    frontMatterTags.EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty))
                .Select(item => item.NormalizedKey)
                .OrderBy(item => item, StringComparer.Ordinal);
            var structuredKeys = TagNormalizer.NormalizeMany(tags)
                .Select(item => item.NormalizedKey)
                .OrderBy(item => item, StringComparer.Ordinal);
            if (!frontKeys.SequenceEqual(
                    structuredKeys,
                    StringComparer.Ordinal))
            {
                throw FrontMatterInvalid(
                    "tags",
                    "Frontmatter tags conflict with structured Knowledge.");
            }
        }
        var definitions = type.Properties
            .Where(item => item.IsEnabled)
            .ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var property in values)
        {
            if (property.Key is "type" or "tags")
            {
                continue;
            }
            if (!definitions.ContainsKey(property.Key))
            {
                throw FrontMatterInvalid(
                    property.Key,
                    "Frontmatter property is not defined by the Knowledge type.");
            }
            var structured = properties.TryGetProperty(
                    property.Key,
                    out var value)
                ? value
                : JsonSerializer.SerializeToElement<object?>(null);
            if (!string.Equals(
                    JsonCanonicalizer.Canonicalize(property.Value),
                    JsonCanonicalizer.Canonicalize(structured),
                    StringComparison.Ordinal))
            {
                throw FrontMatterInvalid(
                    property.Key,
                    "Frontmatter property conflicts with structured Knowledge.");
            }
        }
        return string.Join('\n', lines.Skip(closingIndex + 1));
    }

    private static JsonElement ParseScalar(string value)
    {
        if (value.Length == 0)
        {
            return JsonSerializer.SerializeToElement(string.Empty);
        }
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(value);
        }
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
                throw InvalidAt(
                    property.Name,
                    "Payload contains an unknown or duplicate field.");
            }
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
            ? text
            : throw InvalidAt(name, $"{name} is required.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw InvalidAt(name, $"{name} must be a string.")
            : null;

    private static Guid RequiredGuid(JsonElement element, string name) =>
        OptionalGuid(element, name)
        ?? throw InvalidAt(name, $"{name} is required.");

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
                : throw InvalidAt(name, $"{name} must be a UUID.");
    }

    private static int RequiredInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : throw InvalidAt(name, $"{name} must be an integer.");

    private static JsonElement RequiredObject(JsonElement element, string name) =>
        OptionalObject(element, name)
        ?? throw InvalidAt(name, $"{name} is required.");

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
            : throw InvalidAt(name, $"{name} must be an object.");
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
            throw InvalidAt(name, $"{name} must be a string array.");
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
            : throw InvalidAt(name, $"{name} must be an array.");
    }

    private static VaultException InvalidAt(string fieldPath, string message) =>
        new(
            VaultErrorCode.DraftOperationInvalid,
            message,
            details: new Dictionary<string, object?>
            {
                ["fieldPath"] = fieldPath
            });

    private static VaultException Invalid(string message) =>
        InvalidAt("$", message);

    private static VaultException SourceMetadataInvalid(
        string fieldPath,
        string message) =>
        new(
            VaultErrorCode.SourceMetadataInvalid,
            message,
            details: new Dictionary<string, object?>
            {
                ["fieldPath"] = fieldPath
            });

    private static VaultException FrontMatterInvalid(
        string fieldPath,
        string message) =>
        new(
            VaultErrorCode.FrontMatterModelMismatch,
            message,
            details: new Dictionary<string, object?>
            {
                ["fieldPath"] = fieldPath
            });
}
