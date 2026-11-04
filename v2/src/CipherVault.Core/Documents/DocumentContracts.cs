using System.Text.Json;

namespace CipherVault.Core.Documents;

public sealed record CreateDocumentRequest(
    string Title,
    string? VirtualPath,
    string? Folder,
    VaultDocumentFormat Format,
    string Content,
    IReadOnlyList<string>? Tags);

public sealed record UpdateDocumentRequest(
    string Title,
    string? VirtualPath,
    string Content,
    IReadOnlyList<string> Tags);

public enum VaultPatchTargetKind
{
    Heading,
    FrontMatter
}

public enum VaultPatchOperation
{
    Append,
    Prepend,
    Replace,
    Delete
}

public sealed record DocumentPatchCommand(
    VaultPatchTargetKind TargetKind,
    IReadOnlyList<string>? HeadingPath,
    string? FrontMatterKey,
    VaultPatchOperation Operation,
    string? ContentMarkdown,
    JsonElement? Value,
    bool DryRun);

public sealed record DocumentMetadata(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    VaultDocumentFormat Format,
    int Revision,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DocumentContentChunk(
    Guid DocumentId,
    int Revision,
    string Content,
    int Offset,
    int NextOffset,
    int TotalLength,
    bool Done);

public sealed record DocumentWriteResult(
    Guid DocumentId,
    int Revision,
    string VirtualPath,
    DateTimeOffset CommittedAt,
    Guid? AuditEventId);

public sealed record LinkRebuildResult(
    int DocumentsIndexed,
    int LinksIndexed,
    Guid? AuditEventId);

public sealed record DocumentHeading(
    int Level,
    string Text,
    IReadOnlyList<string> Path);

public sealed record FrontMatterFieldView(
    string Key,
    string ValueType,
    string ValueJson);

public sealed record LinkTargetView(
    Guid DocumentId,
    string Title,
    string VirtualPath);

public sealed record OutgoingLinkView(
    DocumentLinkKind LinkKind,
    string RawTarget,
    string DisplayText,
    string? TargetHeading,
    string ResolutionStatus,
    LinkTargetView? Target,
    IReadOnlyList<LinkTargetView> Candidates,
    int StartOffset,
    int EndOffset,
    int? DestinationStartOffset,
    int? DestinationEndOffset);

public sealed record DocumentMap(
    DocumentMetadata Metadata,
    IReadOnlyList<DocumentHeading> Headings,
    IReadOnlyList<FrontMatterFieldView> FrontMatterFields,
    IReadOnlyList<OutgoingLinkView> OutgoingLinks,
    int BacklinkCount,
    bool HeadingsTruncated,
    bool FrontMatterTruncated,
    bool LinksTruncated);

public sealed record TagItem(string Name, int DocumentCount);

public sealed record TagPage(
    IReadOnlyList<TagItem> Items,
    string? NextCursor);

public sealed record BacklinkItem(
    Guid SourceDocumentId,
    string SourceTitle,
    string SourceVirtualPath,
    int SourceRevision,
    int OccurrenceCount,
    string? FirstTargetHeading,
    DateTimeOffset UpdatedAt);

public sealed record BacklinkPage(
    IReadOnlyList<BacklinkItem> Items,
    string? NextCursor);

public sealed record UnlinkedMentionOccurrence(
    int StartOffset,
    int EndOffset,
    string Context);

public sealed record UnlinkedMentionItem(
    Guid SourceDocumentId,
    string SourceTitle,
    string SourceVirtualPath,
    int SourceRevision,
    int OccurrenceCount,
    IReadOnlyList<UnlinkedMentionOccurrence> Occurrences);

public sealed record UnlinkedMentionPage(
    Guid TargetDocumentId,
    IReadOnlyList<UnlinkedMentionItem> Items,
    string? NextCursor);

public sealed record PatchResult(
    Guid DocumentId,
    int PreviousRevision,
    int Revision,
    bool DryRun,
    bool Changed,
    string UnifiedDiff,
    bool DiffTruncated,
    Guid? AuditEventId);

public sealed record TrashDeleteResult(
    Guid DocumentId,
    int Revision,
    DateTimeOffset DeletedAt,
    DateTimeOffset PurgeEligibleAt,
    Guid? AuditEventId);

public sealed record CatalogItem(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    VaultDocumentFormat Format,
    int Revision,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt);

public sealed record CatalogPage(
    IReadOnlyList<CatalogItem> Items,
    string? NextCursor);

public sealed record CatalogQuery(
    string? Folder,
    VaultDocumentFormat? Format,
    CursorPageRequest Page,
    IReadOnlyList<string>? Tags = null,
    bool OrphanOnly = false);

public sealed record RevisionItem(
    Guid RevisionId,
    Guid DocumentId,
    int Revision,
    string Title,
    string VirtualPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset RetainUntil);

public sealed record RevisionContent(
    Guid DocumentId,
    Guid RevisionId,
    int Revision,
    string Title,
    string VirtualPath,
    VaultDocumentFormat Format,
    IReadOnlyList<string> Tags,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset RetainUntil);

public sealed record RevisionPage(
    IReadOnlyList<RevisionItem> Items,
    string? NextCursor);

public sealed record TrashItem(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    DateTimeOffset DeletedAt,
    DateTimeOffset PurgeEligibleAt);

public sealed record TrashPage(
    IReadOnlyList<TrashItem> Items,
    string? NextCursor);

public sealed record RevisionCleanupResult(
    int DeletedSnapshots,
    Guid? AuditEventId);
