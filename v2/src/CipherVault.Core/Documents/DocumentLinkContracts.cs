namespace CipherVault.Core.Documents;

public enum DocumentLinkKind
{
    Wiki,
    Markdown
}

internal sealed record ParsedDocumentLink(
    DocumentLinkKind Kind,
    string RawTarget,
    string? RawTargetKey,
    string? CanonicalTargetKey,
    string DisplayText,
    string? TargetHeading,
    string? TargetHeadingKey,
    int StartOffset,
    int EndOffset,
    int? DestinationStartOffset,
    int? DestinationEndOffset);

internal sealed record DocumentLinkRecord(
    Guid LinkId,
    Guid SourceDocumentId,
    int SourceRevision,
    Guid? TargetDocumentId,
    DocumentLinkKind Kind,
    string RawTarget,
    string? RawTargetKey,
    string? CanonicalTargetKey,
    string DisplayText,
    string? TargetHeading,
    IReadOnlyList<Guid> CandidateDocumentIds,
    string ResolutionStatus,
    int StartOffset,
    int EndOffset,
    int? DestinationStartOffset,
    int? DestinationEndOffset);
