using System.Text.Json;

namespace CipherVault.Core.Documents;

public enum DocumentLinkResolutionStatus
{
    Resolved,
    Unresolved,
    Ambiguous
}

public sealed record VaultDocumentHeading(
    int Level,
    string Text,
    IReadOnlyList<string> Path);

public sealed record VaultFrontMatterField(
    string Key,
    string ValueType,
    string ValueJson);

public sealed record VaultLinkTarget(
    Guid DocumentId,
    string Title,
    string VirtualPath);

public sealed record VaultOutgoingLink(
    string RawTarget,
    string DisplayText,
    string? TargetHeading,
    string RelationType,
    string ResolutionStatus,
    VaultLinkTarget? Target,
    IReadOnlyList<VaultLinkTarget> Candidates,
    int StartOffset,
    int EndOffset);

public sealed record VaultDocumentMap(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    string Format,
    int Revision,
    IReadOnlyList<VaultDocumentHeading> Headings,
    IReadOnlyList<VaultFrontMatterField> FrontMatterFields,
    IReadOnlyList<VaultOutgoingLink> OutgoingLinks,
    int BacklinkCount,
    DateTimeOffset UpdatedAt);

public sealed record VaultBacklinkItem(
    Guid SourceDocumentId,
    string SourceTitle,
    string SourceVirtualPath,
    int SourceRevision,
    int OccurrenceCount,
    string? FirstTargetHeading,
    DateTimeOffset UpdatedAt);

public sealed record VaultBacklinkPage(
    IReadOnlyList<VaultBacklinkItem> Items,
    string? NextCursor);

public sealed record VaultUnlinkedMentionOccurrence(
    int StartOffset,
    int EndOffset,
    string Context);

public sealed record VaultUnlinkedMentionItem(
    Guid SourceDocumentId,
    string SourceTitle,
    string SourceVirtualPath,
    int SourceRevision,
    int OccurrenceCount,
    IReadOnlyList<VaultUnlinkedMentionOccurrence> Occurrences,
    DateTimeOffset UpdatedAt);

public sealed record VaultUnlinkedMentionPage(
    Guid TargetDocumentId,
    string TargetTitle,
    IReadOnlyList<VaultUnlinkedMentionItem> Items,
    string? NextCursor);

public sealed record VaultTagItem(
    string Name,
    int DocumentCount);

public sealed record VaultTagPage(
    IReadOnlyList<VaultTagItem> Items,
    string? NextCursor);

public sealed record VaultLocalGraphQuery(
    Guid CenterDocumentId,
    int Depth,
    IReadOnlyList<string> Tags,
    string? Folder,
    int MaxNodes,
    int MaxEdges);

public sealed record VaultGraphNode(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    IReadOnlyList<string> Tags,
    int Depth,
    int Degree,
    bool IsCenter);

public sealed record VaultGraphEdge(
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    string RelationType,
    int LinkCount);

public sealed record VaultLocalGraphResult(
    IReadOnlyList<VaultGraphNode> Nodes,
    IReadOnlyList<VaultGraphEdge> Edges,
    bool Truncated,
    int RequestedMaxNodes,
    int RequestedMaxEdges);

public sealed record VaultNotePatchCommand(
    string TargetKind,
    IReadOnlyList<string>? HeadingPath,
    string? FrontMatterKey,
    string Operation,
    string? ContentMarkdown,
    JsonElement? Value,
    bool DryRun);

public sealed record VaultPatchResult(
    Guid DocumentId,
    int PreviousRevision,
    int Revision,
    bool DryRun,
    bool Changed,
    string UnifiedDiff,
    bool DiffTruncated,
    bool LinkIndexCurrent);

public sealed record VaultLinkRebuildResult(
    int DocumentsIndexed,
    int LinksIndexed,
    bool AuditRecorded);
