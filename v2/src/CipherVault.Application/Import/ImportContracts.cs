namespace CipherVault.Application.Import;

public enum ImportPreviewItemState
{
    Ready,
    SkippedConflict,
    Rejected
}

public sealed record ImportPreviewRequest(string SourceRoot);

public sealed record ImportPreviewItem(
    string RelativePath,
    string VirtualPath,
    CipherVault.Core.Documents.VaultDocumentFormat Format,
    long SizeBytes,
    string Sha256,
    ImportPreviewItemState State,
    string? Reason);

public sealed record ImportPreviewResult(
    string Token,
    DateTimeOffset ExpiresAt,
    int ReadyCount,
    int SkippedCount,
    int RejectedCount,
    IReadOnlyList<ImportPreviewItem> Items);

public sealed record ImportCommitRequest(string Token);

public sealed record ImportCommitItem(
    string RelativePath,
    Guid? DocumentId,
    int? Revision,
    string Result,
    string? ErrorCode);

public sealed record ImportCommitResult(
    int Imported,
    int Skipped,
    int Failed,
    IReadOnlyList<ImportCommitItem> Items,
    Guid? AuditEventId);
