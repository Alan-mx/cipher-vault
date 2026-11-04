using CipherVault.Core.Documents;

namespace CipherVault.Core.Relations;

public enum RelationType
{
    Related,
    ExampleOf,
    Implements,
    ContrastsWith,
    DependsOn
}

public enum RelationSuggestionStatus
{
    PendingReview,
    Stale,
    Applied,
    Rejected,
    Expired
}

public enum DocumentRelationStatus
{
    Active,
    Retracted
}

public enum RelationSuggestionApplyResult
{
    None,
    Inserted,
    AlreadyExists
}

public enum RelationStaleReason
{
    SourceRevisionChanged,
    TargetRevisionChanged,
    SourceAndTargetRevisionChanged,
    SourceTrashed,
    TargetTrashed,
    SourceAndTargetTrashed,
    SourceMissing,
    TargetMissing
}

public sealed record RelationEvidence(
    Guid DocumentId,
    int DocumentRevision,
    IReadOnlyList<string> HeadingPath,
    string Excerpt);

public sealed record RelationDocumentReference(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision);

public sealed record RelationDocumentSnapshot(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    bool IsDeleted);

public sealed record RelationSuggestion(
    Guid SuggestionId,
    int SuggestionRevision,
    int PayloadVersion,
    RelationType RelationType,
    Guid SourceDocumentId,
    int SourceDocumentRevision,
    Guid TargetDocumentId,
    int TargetDocumentRevision,
    string Reason,
    string? ModelName,
    string? AgentName,
    string? ClientName,
    IReadOnlyList<RelationEvidence> Evidence,
    RelationSuggestionStatus Status,
    string SourceActor,
    Guid? RelationId,
    RelationSuggestionApplyResult ApplyResult,
    RelationStaleReason? StaleReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt,
    DateTimeOffset? AppliedAt);

public sealed record RelationSuggestionListItem(
    Guid SuggestionId,
    int SuggestionRevision,
    RelationType RelationType,
    RelationSuggestionStatus Status,
    int SourceDocumentRevision,
    int TargetDocumentRevision,
    RelationDocumentReference SourceDocument,
    RelationDocumentReference TargetDocument,
    string SourceActor,
    Guid? RelationId,
    RelationSuggestionApplyResult ApplyResult,
    RelationStaleReason? StaleReasonCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt);

public sealed record RelationSuggestionDetail(
    RelationSuggestionListItem Metadata,
    string Reason,
    string? ModelName,
    string? AgentName,
    string? ClientName,
    IReadOnlyList<RelationEvidence> Evidence,
    DateTimeOffset? AppliedAt);

public sealed record RelationSuggestionPage(
    IReadOnlyList<RelationSuggestionListItem> Items,
    string? NextCursor);

public sealed record RelationSuggestionSummary(
    int PendingReview,
    int Stale,
    int Applied,
    int Rejected,
    int Expired);

public sealed record RelationSuggestionQuery(
    RelationSuggestionStatus Status,
    RelationType? RelationType,
    Guid? SourceDocumentId,
    Guid? TargetDocumentId,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    CursorPageRequest Page);

public sealed record DocumentRelation(
    Guid RelationId,
    RelationType RelationType,
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    DocumentRelationStatus Status,
    string CreatedByActor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RetractedAt,
    string? RetractedByActor);

public enum DocumentRelationDirection
{
    Outgoing,
    Incoming,
    Symmetric
}

public sealed record DocumentRelationItem(
    Guid RelationId,
    RelationType RelationType,
    bool IsDirected,
    DocumentRelationDirection Direction,
    RelationDocumentReference RelatedDocument,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DocumentRelationsView(
    Guid DocumentId,
    IReadOnlyList<DocumentRelationItem> Items,
    bool Truncated);

public sealed record RelationSuggestionApplyOutcome(
    RelationSuggestionStatus Status,
    Guid SuggestionId,
    int SuggestionRevision,
    Guid? RelationId,
    RelationSuggestionApplyResult ApplyResult,
    DateTimeOffset? AppliedAt,
    int? CurrentSourceRevision,
    int? CurrentTargetRevision,
    RelationStaleReason? StaleReason);

public sealed record RelationSuggestionStaleUpdate(
    Guid SuggestionId,
    int SuggestionRevision,
    RelationType RelationType,
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    RelationStaleReason StaleReason);

public sealed record RelationSuggestionMutationResult(
    Guid SuggestionId,
    int SuggestionRevision,
    RelationSuggestionStatus Status,
    DateTimeOffset UpdatedAt);

public sealed record RelationSuggestionDiscardResult(
    Guid SuggestionId,
    DateTimeOffset DiscardedAt);

public sealed record DocumentRelationRetractResult(
    Guid RelationId,
    DocumentRelationStatus Status,
    DateTimeOffset RetractedAt,
    Guid AuditEventId);

public sealed record CreateRelationSuggestionRequest(
    RelationType RelationType,
    Guid SourceDocumentId,
    int SourceDocumentRevision,
    Guid TargetDocumentId,
    int TargetDocumentRevision,
    string Reason,
    string? ModelName,
    string? AgentName,
    string? ClientName,
    IReadOnlyList<RelationEvidence> Evidence);

public sealed record UpdateRelationSuggestionRequest(
    string Reason,
    string? ModelName,
    string? AgentName,
    string? ClientName,
    IReadOnlyList<RelationEvidence> Evidence);

public static class RelationValueMapping
{
    public static string ToDatabaseValue(this RelationType value) =>
        value switch
        {
            RelationType.Related => "related",
            RelationType.ExampleOf => "example_of",
            RelationType.Implements => "implements",
            RelationType.ContrastsWith => "contrasts_with",
            RelationType.DependsOn => "depends_on",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(
        this RelationSuggestionStatus value) =>
        value switch
        {
            RelationSuggestionStatus.PendingReview => "pending_review",
            RelationSuggestionStatus.Stale => "stale",
            RelationSuggestionStatus.Applied => "applied",
            RelationSuggestionStatus.Rejected => "rejected",
            RelationSuggestionStatus.Expired => "expired",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this DocumentRelationStatus value) =>
        value switch
        {
            DocumentRelationStatus.Active => "active",
            DocumentRelationStatus.Retracted => "retracted",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string? ToDatabaseValue(
        this RelationSuggestionApplyResult value) =>
        value switch
        {
            RelationSuggestionApplyResult.None => null,
            RelationSuggestionApplyResult.Inserted => "inserted",
            RelationSuggestionApplyResult.AlreadyExists => "already_exists",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this RelationStaleReason value) =>
        ((RelationStaleReason?)value).ToDatabaseValue()!;

    public static string? ToDatabaseValue(this RelationStaleReason? value) =>
        value switch
        {
            null => null,
            RelationStaleReason.SourceRevisionChanged =>
                "source_revision_changed",
            RelationStaleReason.TargetRevisionChanged =>
                "target_revision_changed",
            RelationStaleReason.SourceAndTargetRevisionChanged =>
                "source_and_target_revision_changed",
            RelationStaleReason.SourceTrashed => "source_trashed",
            RelationStaleReason.TargetTrashed => "target_trashed",
            RelationStaleReason.SourceAndTargetTrashed =>
                "source_and_target_trashed",
            RelationStaleReason.SourceMissing => "source_missing",
            RelationStaleReason.TargetMissing => "target_missing",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static RelationType ParseRelationType(string value) =>
        value switch
        {
            "related" => RelationType.Related,
            "example_of" => RelationType.ExampleOf,
            "implements" => RelationType.Implements,
            "contrasts_with" => RelationType.ContrastsWith,
            "depends_on" => RelationType.DependsOn,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static RelationSuggestionStatus ParseSuggestionStatus(
        string value) =>
        value switch
        {
            "pending_review" => RelationSuggestionStatus.PendingReview,
            "stale" => RelationSuggestionStatus.Stale,
            "applied" => RelationSuggestionStatus.Applied,
            "rejected" => RelationSuggestionStatus.Rejected,
            "expired" => RelationSuggestionStatus.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static DocumentRelationStatus ParseDocumentRelationStatus(
        string value) =>
        value switch
        {
            "active" => DocumentRelationStatus.Active,
            "retracted" => DocumentRelationStatus.Retracted,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static RelationSuggestionApplyResult ParseApplyResult(
        string? value) =>
        value switch
        {
            null => RelationSuggestionApplyResult.None,
            "inserted" => RelationSuggestionApplyResult.Inserted,
            "already_exists" =>
                RelationSuggestionApplyResult.AlreadyExists,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static RelationStaleReason? ParseStaleReason(string? value) =>
        value switch
        {
            null => null,
            "source_revision_changed" =>
                RelationStaleReason.SourceRevisionChanged,
            "target_revision_changed" =>
                RelationStaleReason.TargetRevisionChanged,
            "source_and_target_revision_changed" =>
                RelationStaleReason.SourceAndTargetRevisionChanged,
            "source_trashed" => RelationStaleReason.SourceTrashed,
            "target_trashed" => RelationStaleReason.TargetTrashed,
            "source_and_target_trashed" =>
                RelationStaleReason.SourceAndTargetTrashed,
            "source_missing" => RelationStaleReason.SourceMissing,
            "target_missing" => RelationStaleReason.TargetMissing,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static bool IsDirected(this RelationType value) =>
        value is not (RelationType.Related or RelationType.ContrastsWith);
}

public static class RelationDirection
{
    public static (Guid SourceDocumentId, Guid TargetDocumentId) Canonicalize(
        RelationType relationType,
        Guid sourceDocumentId,
        Guid targetDocumentId)
    {
        if (relationType.IsDirected()
            || string.CompareOrdinal(
                sourceDocumentId.ToString("D"),
                targetDocumentId.ToString("D")) < 0)
        {
            return (sourceDocumentId, targetDocumentId);
        }
        return (targetDocumentId, sourceDocumentId);
    }
}
