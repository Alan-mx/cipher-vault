namespace CipherVault.Core.RelatedKnowledge;

public sealed record RelatedDocumentItem(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    IReadOnlyList<string> Tags,
    double Score,
    IReadOnlyList<RelatedReason> Reasons,
    DateTimeOffset UpdatedAt);

public sealed record RelatedReason(
    string Code,
    string Label);
