using System.Text.Json;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Api.Contracts;

public sealed record KnowledgeModelUpdateApiRequest(
    KnowledgeTypeApiUpdate[]? KnowledgeTypes,
    KnowledgeTemplateApiUpdate[]? Templates,
    RelationTypeApiUpdate[]? RelationTypes,
    SourceRuleApiUpdate[]? SourceRules,
    WorkflowApiUpdate[]? Workflows)
{
    public KnowledgeModelUpdate ToDomain()
    {
        return new KnowledgeModelUpdate(
            Required(KnowledgeTypes, nameof(KnowledgeTypes))
                .Select(item => item.ToDomain())
                .ToArray(),
            Required(Templates, nameof(Templates))
                .Select(item => item.ToDomain())
                .ToArray(),
            Required(RelationTypes, nameof(RelationTypes))
                .Select(item => item.ToDomain())
                .ToArray(),
            Required(SourceRules, nameof(SourceRules))
                .Select(item => item.ToDomain())
                .ToArray(),
            Required(Workflows, nameof(Workflows))
                .Select(item => item.ToDomain())
                .ToArray());
    }

    private static T[] Required<T>(T[]? value, string name) =>
        value ?? throw Invalid($"{name} is required.");

    private static ApiRequestException Invalid(string message) =>
        new(400, ApiErrorCodes.KnowledgeModelInvalid, message);
}

public sealed record KnowledgeTypeApiUpdate(
    Guid? TypeId,
    string TypeKey,
    string Name,
    string? Description,
    bool IsEnabled,
    int SortOrder,
    KnowledgePropertyApiUpdate[]? Properties)
{
    public KnowledgeTypeUpdate ToDomain() =>
        new(
            TypeId,
            Required(TypeKey, nameof(TypeKey)),
            Required(Name, nameof(Name)),
            Description,
            IsEnabled,
            SortOrder,
            Properties?
                .Select(item => item.ToDomain())
                .ToArray()
            ?? throw Invalid("Properties is required."));

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Invalid($"{name} is required.")
            : value.Trim();

    private static ApiRequestException Invalid(string message) =>
        new(400, ApiErrorCodes.KnowledgeModelInvalid, message);
}

public sealed record KnowledgePropertyApiUpdate(
    Guid? PropertyId,
    string PropertyKey,
    string Name,
    string ValueType,
    bool Required,
    JsonElement? DefaultValue,
    JsonElement? Constraints,
    bool IsEnabled,
    int SortOrder)
{
    public KnowledgePropertyUpdate ToDomain() =>
        new(
            PropertyId,
            RequiredText(PropertyKey, nameof(PropertyKey)),
            RequiredText(Name, nameof(Name)),
            RequiredText(ValueType, nameof(ValueType)),
            Required,
            DefaultValue,
            RequireObject(Constraints),
            IsEnabled,
            SortOrder);

    private static string RequiredText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Invalid($"{name} is required.")
            : value.Trim();

    private static JsonElement RequireObject(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Constraints must be an object.");
        }
        return value.Value.Clone();
    }

    private static ApiRequestException Invalid(string message) =>
        new(400, ApiErrorCodes.KnowledgeModelInvalid, message);
}

public sealed record KnowledgeTemplateApiUpdate(
    Guid? TemplateId,
    Guid KnowledgeTypeId,
    string TemplateKey,
    string Name,
    string? Description,
    string BodyTemplate,
    JsonElement? Fields,
    bool IsEnabled,
    int SortOrder)
{
    public KnowledgeTemplateUpdate ToDomain() =>
        new(
            TemplateId,
            KnowledgeTypeId,
            Required(TemplateKey, nameof(TemplateKey)),
            Required(Name, nameof(Name)),
            Description,
            BodyTemplate ?? string.Empty,
            RequireArray(Fields),
            IsEnabled,
            SortOrder);

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Invalid($"{name} is required.")
            : value.Trim();

    private static JsonElement RequireArray(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("Fields must be an array.");
        }
        return value.Value.Clone();
    }

    private static ApiRequestException Invalid(string message) =>
        new(400, ApiErrorCodes.KnowledgeModelInvalid, message);
}

public sealed record RelationTypeApiUpdate(
    Guid? RelationTypeId,
    string RelationKey,
    string Name,
    string? Description,
    bool IsDirected,
    bool IsEnabled,
    int SortOrder)
{
    public RelationTypeUpdate ToDomain() =>
        new(
            RelationTypeId,
            Required(RelationKey, nameof(RelationKey)),
            Required(Name, nameof(Name)),
            Description,
            IsDirected,
            IsEnabled,
            SortOrder);

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Invalid($"{name} is required.")
            : value.Trim();

    private static ApiRequestException Invalid(string message) =>
        new(400, ApiErrorCodes.KnowledgeModelInvalid, message);
}

public sealed record SourceRuleApiUpdate(
    Guid? SourceRuleId,
    string SourceKind,
    JsonElement? MetadataSchema,
    string[]? SupportedLocatorTypes,
    bool IsEnabled)
{
    public SourceRuleUpdate ToDomain() =>
        new(
            SourceRuleId,
            ParseSourceKind(SourceKind),
            RequireObject(MetadataSchema),
            Required(SupportedLocatorTypes, nameof(SupportedLocatorTypes))
                .Select(ParseLocatorType)
                .ToArray(),
            IsEnabled);

    private static Core.KnowledgeBase.SourceKind ParseSourceKind(string value) =>
        value switch
        {
            "web_page" => Core.KnowledgeBase.SourceKind.WebPage,
            "local_file" => Core.KnowledgeBase.SourceKind.LocalFile,
            "book" => Core.KnowledgeBase.SourceKind.Book,
            "podcast" => Core.KnowledgeBase.SourceKind.Podcast,
            "other" => Core.KnowledgeBase.SourceKind.Other,
            _ => throw Invalid("SourceKind is invalid.")
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

    private static T[] Required<T>(T[]? value, string name) =>
        value ?? throw Invalid($"{name} is required.");

    private static JsonElement RequireObject(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("MetadataSchema must be an object.");
        }
        return value.Value.Clone();
    }

    private static ApiRequestException Invalid(string message) =>
        new(400, ApiErrorCodes.KnowledgeModelInvalid, message);
}

public sealed record WorkflowApiUpdate(
    Guid? WorkflowId,
    string WorkflowKey,
    string Name,
    string? Description,
    string[]? TargetObjectTypes,
    JsonElement? Constraints,
    bool IsEnabled,
    int SortOrder)
{
    public WorkflowUpdate ToDomain() =>
        new(
            WorkflowId,
            Required(WorkflowKey, nameof(WorkflowKey)),
            Required(Name, nameof(Name)),
            Description,
            Required(TargetObjectTypes, nameof(TargetObjectTypes))
                .Select(ParseTargetObjectType)
                .ToArray(),
            RequireObject(Constraints),
            IsEnabled,
            SortOrder);

    private static DraftTargetObjectType ParseTargetObjectType(string value) =>
        value switch
        {
            "knowledge" => DraftTargetObjectType.Knowledge,
            "source" => DraftTargetObjectType.Source,
            "relation" => DraftTargetObjectType.Relation,
            _ => throw Invalid("Target object type is invalid.")
        };

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Invalid($"{name} is required.")
            : value.Trim();

    private static T[] Required<T>(T[]? value, string name) =>
        value ?? throw Invalid($"{name} is required.");

    private static JsonElement RequireObject(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Constraints must be an object.");
        }
        return value.Value.Clone();
    }

    private static ApiRequestException Invalid(string message) =>
        new(400, ApiErrorCodes.KnowledgeModelInvalid, message);
}
