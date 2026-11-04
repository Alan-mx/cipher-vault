using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Drafts;

public enum DocumentDraftType
{
    Create,
    Update,
    Trash
}

public enum DocumentDraftStatus
{
    Draft,
    PendingReview,
    Conflict,
    Applied,
    Rejected,
    Discarded,
    Expired
}

public sealed record DocumentDraftQuery(
    DocumentDraftStatus? Status,
    DocumentDraftType? Type,
    Guid? TargetDocumentId,
    CursorPageRequest Page);

public sealed record DocumentDraftMetadata(
    Guid DraftId,
    int DraftRevision,
    DocumentDraftType DraftType,
    DocumentDraftStatus Status,
    Guid? TargetDocumentId,
    int? BaseDocumentRevision,
    Guid? FutureDocumentId,
    string SourceActor,
    string? Title,
    string? VirtualPath,
    VaultDocumentFormat? Format,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt,
    Guid? AppliedDocumentId,
    int? AppliedDocumentRevision);

public sealed record DocumentDraftPreview(
    string Content,
    IReadOnlyList<string> Tags,
    string UnifiedDiff,
    bool DiffTruncated,
    int OperationCount);

public sealed record DocumentDraftDetail(
    DocumentDraftMetadata Metadata,
    JsonElement Payload,
    DocumentDraftPreview Preview);

public sealed record DocumentDraftPage(
    IReadOnlyList<DocumentDraftMetadata> Items,
    string? NextCursor);

public sealed record DocumentDraftMutationResult(
    Guid DraftId,
    int DraftRevision,
    DocumentDraftStatus Status,
    Guid? FutureDocumentId,
    DateTimeOffset UpdatedAt);

public sealed record DocumentDraftApplyResult(
    Guid DraftId,
    int DraftRevision,
    DocumentDraftStatus Status,
    DocumentWriteResult DocumentWriteResult);

public sealed record CreateDocumentDraftRequest(
    DocumentDraftType DraftType,
    Guid? TargetDocumentId,
    int? BaseDocumentRevision,
    JsonElement Payload);

public sealed record DocumentDraftSnapshot(
    Guid DocumentId,
    int Revision,
    string Title,
    string VirtualPath,
    VaultDocumentFormat Format,
    string Content,
    IReadOnlyList<string> Tags);

public sealed record DocumentDraftWorkingCopy(
    Guid? DocumentId,
    string Title,
    string VirtualPath,
    VaultDocumentFormat Format,
    string Content,
    IReadOnlyList<string> Tags);

public sealed record DocumentDraftPreviewComputation(
    DocumentDraftWorkingCopy WorkingCopy,
    string UnifiedDiff,
    bool DiffTruncated,
    int OperationCount);

public sealed record DocumentDraftRecord(
    Guid DraftId,
    int DraftRevision,
    DocumentDraftType DraftType,
    DocumentDraftStatus Status,
    Guid? TargetDocumentId,
    int? BaseDocumentRevision,
    int PayloadVersion,
    string PayloadJson,
    string? BaseSnapshotJson,
    string SourceActor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt,
    Guid? AppliedDocumentId,
    int? AppliedDocumentRevision,
    DateTimeOffset? AppliedAt);

public sealed record DocumentDraftRecordPage(
    IReadOnlyList<DocumentDraftRecord> Items,
    string? NextCursor);

public static class DocumentDraftValueMapping
{
    public static string ToDatabaseValue(this DocumentDraftType value) =>
        value switch
        {
            DocumentDraftType.Create => "create",
            DocumentDraftType.Update => "update",
            DocumentDraftType.Trash => "trash",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this DocumentDraftStatus value) =>
        value switch
        {
            DocumentDraftStatus.Draft => "draft",
            DocumentDraftStatus.PendingReview => "pending_review",
            DocumentDraftStatus.Conflict => "conflict",
            DocumentDraftStatus.Applied => "applied",
            DocumentDraftStatus.Rejected => "rejected",
            DocumentDraftStatus.Discarded => "discarded",
            DocumentDraftStatus.Expired => "expired",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static DocumentDraftType ParseDraftType(string value) =>
        value switch
        {
            "create" => DocumentDraftType.Create,
            "update" => DocumentDraftType.Update,
            "trash" => DocumentDraftType.Trash,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static DocumentDraftStatus ParseDraftStatus(string value) =>
        value switch
        {
            "draft" => DocumentDraftStatus.Draft,
            "pending_review" => DocumentDraftStatus.PendingReview,
            "conflict" => DocumentDraftStatus.Conflict,
            "applied" => DocumentDraftStatus.Applied,
            "rejected" => DocumentDraftStatus.Rejected,
            "discarded" => DocumentDraftStatus.Discarded,
            "expired" => DocumentDraftStatus.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static bool IsTerminal(this DocumentDraftStatus value) =>
        value is
            DocumentDraftStatus.Applied
            or DocumentDraftStatus.Rejected
            or DocumentDraftStatus.Discarded
            or DocumentDraftStatus.Expired;
}
