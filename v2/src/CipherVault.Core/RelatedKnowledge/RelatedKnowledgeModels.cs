using System.Text.Json;

namespace CipherVault.Core.RelatedKnowledge;

internal sealed record RelatedKnowledgeDocument(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    JsonElement FrontMatter,
    IReadOnlyList<RelatedTag> Tags,
    DateTimeOffset UpdatedAt);

internal sealed record RelatedTag(
    string Name,
    string NormalizedKey);

internal enum RelatedScalarKind
{
    String,
    Number,
    Boolean
}

internal sealed record RelatedScalarProperty(
    string Key,
    RelatedScalarKind Kind,
    string? StringValue,
    double? NumberValue,
    bool? BooleanValue);

internal sealed record RelatedFrontMatterProfile(
    string? Project,
    string? Type,
    IReadOnlyList<RelatedScalarProperty> Properties);

internal sealed record RelatedCandidatePool(
    IReadOnlySet<Guid> SameProject,
    IReadOnlySet<Guid> SameType,
    IReadOnlySet<Guid> SharedTags,
    IReadOnlySet<Guid> SharedProperties,
    IReadOnlySet<Guid> SimilarTitle,
    IReadOnlySet<Guid> TwoHop);

internal sealed record RelatedSignalSet(
    bool SameProject,
    bool SameType,
    IReadOnlyList<string> SharedTagNames,
    IReadOnlyList<string> SharedPropertyNames,
    bool TwoHop,
    double TitleSimilarity)
{
    internal bool HasNonTitleSignal =>
        SameProject
        || SameType
        || SharedTagNames.Count > 0
        || SharedPropertyNames.Count > 0
        || TwoHop;
}

internal sealed record RelatedReasonContribution(
    string Code,
    string Label,
    double Contribution);

internal sealed record RelatedScoredCandidate(
    RelatedKnowledgeDocument Document,
    double Score,
    IReadOnlyList<RelatedReasonContribution> Reasons);
