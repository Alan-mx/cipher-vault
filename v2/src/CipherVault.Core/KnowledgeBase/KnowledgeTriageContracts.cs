namespace CipherVault.Core.KnowledgeBase;

public enum KnowledgeTriageReason
{
    MissingRequiredProperty,
    MissingSource,
    Stale
}

public sealed record KnowledgeTriageQuery(
    KnowledgeTriageReason? Reason,
    Guid? KnowledgeTypeId,
    int StaleDays,
    string? Search,
    int PageSize,
    string? Cursor);

public sealed record KnowledgeTriageItem(
    Guid KnowledgeId,
    string Title,
    Guid KnowledgeTypeId,
    string TypeKey,
    string TypeName,
    IReadOnlyList<string> Tags,
    int Revision,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<KnowledgeTriageReason> ReasonCodes,
    IReadOnlyList<string> MissingPropertyKeys,
    int SourceReferenceCount);

public sealed record KnowledgeTriageCounts(
    int MissingSource,
    int MissingRequiredProperty,
    int Stale,
    int TotalDistinct,
    int Draft,
    int Conflict);

public sealed record KnowledgeTriagePage(
    IReadOnlyList<KnowledgeTriageItem> Items,
    string? NextCursor,
    bool HasMore,
    KnowledgeTriageCounts Counts);
