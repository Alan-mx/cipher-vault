namespace CipherVault.Core.Knowledge;

internal sealed record KnowledgeSourceRow(
    Guid DocumentId,
    string? FacetKey,
    string? Label);

internal sealed record KnowledgeFacetRow(
    KnowledgeViewField Field,
    string? Key,
    string Label,
    int DocumentCount,
    bool IsUnset);

internal sealed record KnowledgeViewCursor(
    DateTimeOffset UpdatedAt,
    Guid DocumentId);
