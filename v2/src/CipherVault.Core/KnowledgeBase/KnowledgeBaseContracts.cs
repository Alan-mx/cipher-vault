using System.Text.Json;

namespace CipherVault.Core.KnowledgeBase;

public enum SourceKind
{
    WebPage,
    LocalFile,
    Book,
    Podcast,
    Other
}

public enum SourceStatus
{
    Active,
    Archived
}

public enum SourceContentFormat
{
    Markdown,
    Text
}

public enum SourceLocatorType
{
    Section,
    Paragraph,
    Page,
    Timestamp
}

public enum KnowledgeStatus
{
    Active,
    Archived
}

public enum KnowledgeRelationStatus
{
    Active,
    Retracted
}

public enum DraftTargetObjectType
{
    Knowledge,
    Source,
    Relation
}

public enum DraftOperationType
{
    Create,
    Update,
    Archive,
    Restore,
    Retract
}

public enum DraftStatus
{
    Draft,
    PendingReview,
    Conflict,
    Applied,
    Rejected,
    Discarded,
    Expired
}

public enum KnowledgeSearchMode
{
    Keyword,
    Fulltext
}

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore,
    int? TotalCount);

public sealed record KnowledgeModelSnapshot(
    int Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<KnowledgeTypeDefinition> KnowledgeTypes,
    IReadOnlyList<KnowledgeTemplateDefinition> Templates,
    IReadOnlyList<RelationTypeDefinition> RelationTypes,
    IReadOnlyList<SourceRuleDefinition> SourceRules,
    IReadOnlyList<WorkflowDefinition> Workflows);

public sealed record KnowledgeModelUpdate(
    IReadOnlyList<KnowledgeTypeUpdate> KnowledgeTypes,
    IReadOnlyList<KnowledgeTemplateUpdate> Templates,
    IReadOnlyList<RelationTypeUpdate> RelationTypes,
    IReadOnlyList<SourceRuleUpdate> SourceRules,
    IReadOnlyList<WorkflowUpdate> Workflows);

public sealed record KnowledgeTypeUpdate(
    Guid? TypeId,
    string Key,
    string Name,
    string? Description,
    bool IsEnabled,
    int SortOrder,
    IReadOnlyList<KnowledgePropertyUpdate> Properties);

public sealed record KnowledgePropertyUpdate(
    Guid? PropertyId,
    string Key,
    string Name,
    string ValueType,
    bool Required,
    JsonElement? DefaultValue,
    JsonElement Constraints,
    bool IsEnabled,
    int SortOrder);

public sealed record KnowledgeTemplateUpdate(
    Guid? TemplateId,
    Guid KnowledgeTypeId,
    string Key,
    string Name,
    string? Description,
    string BodyTemplate,
    JsonElement Fields,
    bool IsEnabled,
    int SortOrder);

public sealed record RelationTypeUpdate(
    Guid? RelationTypeId,
    string Key,
    string Name,
    string? Description,
    bool IsDirected,
    bool IsEnabled,
    int SortOrder);

public sealed record SourceRuleUpdate(
    Guid? SourceRuleId,
    SourceKind Kind,
    JsonElement MetadataSchema,
    IReadOnlyList<SourceLocatorType> SupportedLocatorTypes,
    bool IsEnabled);

public sealed record WorkflowUpdate(
    Guid? WorkflowId,
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<DraftTargetObjectType> TargetObjectTypes,
    JsonElement Constraints,
    bool IsEnabled,
    int SortOrder);

public sealed record KnowledgeTypeDefinition(
    Guid TypeId,
    string Key,
    string Name,
    string? Description,
    bool IsEnabled,
    int SortOrder,
    IReadOnlyList<KnowledgePropertyDefinition> Properties);

public sealed record KnowledgePropertyDefinition(
    Guid PropertyId,
    string Key,
    string Name,
    string ValueType,
    bool Required,
    JsonElement? DefaultValue,
    JsonElement Constraints,
    bool IsEnabled,
    int SortOrder);

public sealed record KnowledgeTemplateDefinition(
    Guid TemplateId,
    Guid KnowledgeTypeId,
    string Key,
    string Name,
    string? Description,
    string BodyTemplate,
    JsonElement Fields,
    bool IsEnabled,
    int SortOrder);

public sealed record RelationTypeDefinition(
    Guid RelationTypeId,
    string Key,
    string Name,
    string? Description,
    bool IsDirected,
    bool IsEnabled,
    int SortOrder);

public sealed record SourceRuleDefinition(
    Guid SourceRuleId,
    SourceKind Kind,
    JsonElement MetadataSchema,
    IReadOnlyList<SourceLocatorType> SupportedLocatorTypes,
    bool IsEnabled);

public sealed record WorkflowDefinition(
    Guid WorkflowId,
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<DraftTargetObjectType> TargetObjectTypes,
    JsonElement Constraints,
    bool IsEnabled,
    int SortOrder);

public sealed record SourceSummary(
    Guid SourceId,
    SourceKind Kind,
    string Title,
    SourceStatus Status,
    int CurrentRevision,
    DateTimeOffset UpdatedAt);

public sealed record SourceDetail(
    Guid SourceId,
    SourceKind Kind,
    string Title,
    JsonElement Metadata,
    SourceStatus Status,
    int CurrentRevision,
    Guid CurrentRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record SourceRevisionSummary(
    Guid SourceId,
    Guid SourceRevisionId,
    int Revision,
    SourceKind Kind,
    string Title,
    JsonElement Metadata,
    SourceContentFormat ContentFormat,
    int ContentLength,
    DateTimeOffset CreatedAt);

public sealed record SourceContentChunk(
    Guid SourceId,
    int SourceRevision,
    SourceContentFormat ContentFormat,
    int Offset,
    int NextOffset,
    int TotalLength,
    string Text,
    bool Done);

public sealed record SourceLocatorView(
    Guid LocatorId,
    SourceLocatorType Type,
    JsonElement Value,
    string? Label,
    int StartOffset,
    int EndOffset,
    int Ordinal,
    Guid? ParentLocatorId);

public sealed record SourceLocatorContent(
    Guid SourceId,
    int SourceRevision,
    SourceLocatorView Locator,
    int Offset,
    int NextOffset,
    int Length,
    string Text,
    bool Done);

public sealed record SourceQuery(
    SourceStatus? Status,
    SourceKind? Kind,
    string? Query,
    int PageSize,
    string? Cursor);

public sealed record KnowledgeSummary(
    Guid KnowledgeId,
    string Title,
    string Summary,
    Guid KnowledgeTypeId,
    string KnowledgeTypeKey,
    IReadOnlyList<string> Tags,
    KnowledgeStatus Status,
    int Revision,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeQuery(
    KnowledgeStatus? Status,
    Guid? TypeId,
    IReadOnlyList<string>? Tags,
    int PageSize,
    string? Cursor);

public sealed record KnowledgeSearchQuery(
    string Query,
    KnowledgeSearchMode Mode,
    IReadOnlyList<Guid>? TypeIds,
    IReadOnlyList<Guid>? SourceIds,
    IReadOnlyList<string>? Tags,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    int PageSize,
    string? Cursor);

public sealed record SourceReferenceView(
    Guid SourceReferenceId,
    Guid SourceId,
    int SourceRevision,
    SourceLocatorType LocatorType,
    JsonElement LocatorValue,
    string Excerpt,
    JsonElement ExtractionMetadata);

public sealed record KnowledgeDetail(
    Guid KnowledgeId,
    string Title,
    string Summary,
    Guid KnowledgeTypeId,
    string KnowledgeTypeKey,
    Guid? TemplateId,
    KnowledgeStatus Status,
    int CurrentRevision,
    Guid CurrentRevisionId,
    JsonElement Properties,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SourceReferenceView> SourceReferences,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeRevisionSummary(
    Guid KnowledgeRevisionId,
    int Revision,
    string Title,
    Guid KnowledgeTypeId,
    Guid? TemplateId,
    int KnowledgeModelVersion,
    DateTimeOffset CreatedAt);

public sealed record KnowledgeContentChunk(
    Guid KnowledgeId,
    int KnowledgeRevision,
    int Offset,
    int NextOffset,
    int TotalLength,
    string Markdown,
    bool Done);

public sealed record KnowledgeDocument(
    Guid KnowledgeId,
    int Revision,
    string FileName,
    string Content);

public sealed record KnowledgeSearchHit(
    Guid KnowledgeId,
    string Title,
    Guid KnowledgeTypeId,
    string KnowledgeTypeKey,
    string Summary,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SourceReferenceView> SourceReferences,
    int Revision,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeRelationItem(
    Guid RelationId,
    Guid RelationTypeId,
    string RelationTypeKey,
    Guid SourceKnowledgeId,
    Guid TargetKnowledgeId,
    int SourceKnowledgeRevision,
    int TargetKnowledgeRevision,
    KnowledgeRelationStatus Status,
    string Rationale,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LocalKnowledgeGraph(
    IReadOnlyList<LocalKnowledgeGraphNode> Nodes,
    IReadOnlyList<LocalKnowledgeGraphEdge> Edges,
    bool Truncated);

public sealed record LocalKnowledgeGraphNode(
    Guid KnowledgeId,
    string Title,
    Guid KnowledgeTypeId,
    IReadOnlyList<string> Tags,
    int Revision,
    int Degree,
    int Depth,
    bool IsCenter);

public sealed record LocalKnowledgeGraphEdge(
    Guid RelationId,
    Guid RelationTypeId,
    Guid SourceKnowledgeId,
    Guid TargetKnowledgeId,
    bool IsDirected);

public sealed record DraftRecord(
    Guid DraftId,
    int DraftRevision,
    DraftTargetObjectType TargetObjectType,
    DraftOperationType OperationType,
    Guid? TargetId,
    int? BaseRevision,
    int BaseModelVersion,
    int PayloadVersion,
    JsonElement Payload,
    JsonElement? BaseSnapshot,
    DraftStatus Status,
    string SourceActor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt,
    DraftTargetObjectType? AppliedObjectType,
    Guid? AppliedObjectId,
    int? AppliedObjectRevision,
    DateTimeOffset? AppliedAt);

public sealed record DraftSummary(
    int Working,
    int PendingReview,
    int History,
    int Inbox);

public sealed record DraftListItem(
    Guid DraftId,
    int DraftRevision,
    DraftTargetObjectType TargetObjectType,
    DraftOperationType OperationType,
    Guid? TargetId,
    int? BaseRevision,
    DraftStatus Status,
    string SourceActor,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum DraftFieldChangeKind
{
    New,
    Added,
    Modified,
    Removed,
    BaselineUnavailable
}

public enum DraftValidationSeverity
{
    Warning,
    Error
}

public sealed record DraftFieldChange(
    string FieldPath,
    DraftFieldChangeKind Kind,
    string? Before,
    string? After,
    string ValueType,
    bool BeforeTruncated,
    bool AfterTruncated);

public sealed record DraftValidationIssue(
    string FieldPath,
    string Message,
    DraftValidationSeverity Severity);

public sealed record DraftPreview(
    string Summary,
    string Diff,
    bool DiffTruncated,
    IReadOnlyList<SourceReferenceView> SourceReferences,
    IReadOnlyList<DraftFieldChange> Changes,
    bool ChangesTruncated,
    IReadOnlyList<DraftValidationIssue> ValidationIssues);

public sealed record DraftDetail(
    DraftRecord Record,
    DraftPreview Preview);

public sealed record DraftQuery(
    DraftStatus? Status,
    DraftTargetObjectType? TargetObjectType,
    DraftOperationType? OperationType,
    string? SourceActor,
    Guid? TargetId,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    int PageSize,
    string? Cursor);

public sealed record CreateDraftInput(
    DraftTargetObjectType TargetObjectType,
    DraftOperationType OperationType,
    Guid? TargetId,
    int? BaseRevision,
    JsonElement Payload);

public sealed record DraftMutation(
    Guid DraftId,
    int DraftRevision,
    DraftStatus Status,
    DraftTargetObjectType TargetObjectType,
    DraftOperationType OperationType,
    DateTimeOffset UpdatedAt);

public sealed record DraftApplyResult(
    Guid DraftId,
    int DraftRevision,
    DraftStatus Status,
    DraftTargetObjectType ObjectType,
    Guid ObjectId,
    int ObjectRevision);

public sealed record SourceMutationPayload(
    SourceKind Kind,
    string Title,
    JsonElement Metadata,
    SourceContentFormat ContentFormat,
    string ContentText);

public sealed record SourceReferenceInput(
    Guid SourceId,
    int SourceRevision,
    SourceLocatorType LocatorType,
    JsonElement LocatorValue,
    string Excerpt,
    JsonElement ExtractionMetadata);

public sealed record KnowledgeMutationPayload(
    string Title,
    string Summary,
    Guid KnowledgeTypeId,
    Guid? TemplateId,
    string ContentMarkdown,
    JsonElement Properties,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SourceReferenceInput> SourceReferences);

public sealed record RelationMutationPayload(
    Guid RelationTypeId,
    Guid SourceKnowledgeId,
    Guid TargetKnowledgeId,
    int SourceKnowledgeRevision,
    int TargetKnowledgeRevision,
    string Rationale,
    JsonElement Evidence);

public sealed record DashboardView(
    IReadOnlyList<KnowledgeSummary> RecentKnowledge,
    IReadOnlyList<SourceSummary> RecentSources,
    IReadOnlyList<DraftListItem> PendingDrafts,
    IReadOnlyList<DraftListItem> Inbox,
    IReadOnlyList<KnowledgeRelationItem> RecentRelationChanges,
    IReadOnlyDictionary<string, int> Counts);

public static class KnowledgeBaseValueMapping
{
    public static string ToDatabaseValue(this SourceKind value) =>
        value switch
        {
            SourceKind.WebPage => "web_page",
            SourceKind.LocalFile => "local_file",
            SourceKind.Book => "book",
            SourceKind.Podcast => "podcast",
            SourceKind.Other => "other",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static SourceKind ParseSourceKind(string value) =>
        value switch
        {
            "web_page" => SourceKind.WebPage,
            "local_file" => SourceKind.LocalFile,
            "book" => SourceKind.Book,
            "podcast" => SourceKind.Podcast,
            "other" => SourceKind.Other,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this SourceStatus value) =>
        value switch
        {
            SourceStatus.Active => "active",
            SourceStatus.Archived => "archived",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static SourceStatus ParseSourceStatus(string value) =>
        value switch
        {
            "active" => SourceStatus.Active,
            "archived" => SourceStatus.Archived,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this SourceContentFormat value) =>
        value switch
        {
            SourceContentFormat.Markdown => "markdown",
            SourceContentFormat.Text => "text",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static SourceContentFormat ParseContentFormat(string value) =>
        value switch
        {
            "markdown" => SourceContentFormat.Markdown,
            "text" => SourceContentFormat.Text,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this SourceLocatorType value) =>
        value switch
        {
            SourceLocatorType.Section => "section",
            SourceLocatorType.Paragraph => "paragraph",
            SourceLocatorType.Page => "page",
            SourceLocatorType.Timestamp => "timestamp",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static SourceLocatorType ParseLocatorType(string value) =>
        value switch
        {
            "section" => SourceLocatorType.Section,
            "paragraph" => SourceLocatorType.Paragraph,
            "page" => SourceLocatorType.Page,
            "timestamp" => SourceLocatorType.Timestamp,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this KnowledgeStatus value) =>
        value switch
        {
            KnowledgeStatus.Active => "active",
            KnowledgeStatus.Archived => "archived",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static KnowledgeStatus ParseKnowledgeStatus(string value) =>
        value switch
        {
            "active" => KnowledgeStatus.Active,
            "archived" => KnowledgeStatus.Archived,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this DraftTargetObjectType value) =>
        value switch
        {
            DraftTargetObjectType.Knowledge => "knowledge",
            DraftTargetObjectType.Source => "source",
            DraftTargetObjectType.Relation => "relation",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static DraftTargetObjectType ParseTargetObjectType(string value) =>
        value switch
        {
            "knowledge" => DraftTargetObjectType.Knowledge,
            "source" => DraftTargetObjectType.Source,
            "relation" => DraftTargetObjectType.Relation,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this DraftOperationType value) =>
        value switch
        {
            DraftOperationType.Create => "create",
            DraftOperationType.Update => "update",
            DraftOperationType.Archive => "archive",
            DraftOperationType.Restore => "restore",
            DraftOperationType.Retract => "retract",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static DraftOperationType ParseOperationType(string value) =>
        value switch
        {
            "create" => DraftOperationType.Create,
            "update" => DraftOperationType.Update,
            "archive" => DraftOperationType.Archive,
            "restore" => DraftOperationType.Restore,
            "retract" => DraftOperationType.Retract,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this DraftStatus value) =>
        value switch
        {
            DraftStatus.Draft => "draft",
            DraftStatus.PendingReview => "pending_review",
            DraftStatus.Conflict => "conflict",
            DraftStatus.Applied => "applied",
            DraftStatus.Rejected => "rejected",
            DraftStatus.Discarded => "discarded",
            DraftStatus.Expired => "expired",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static DraftStatus ParseDraftStatus(string value) =>
        value switch
        {
            "draft" => DraftStatus.Draft,
            "pending_review" => DraftStatus.PendingReview,
            "conflict" => DraftStatus.Conflict,
            "applied" => DraftStatus.Applied,
            "rejected" => DraftStatus.Rejected,
            "discarded" => DraftStatus.Discarded,
            "expired" => DraftStatus.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}
