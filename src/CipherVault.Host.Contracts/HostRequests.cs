namespace CipherVault.Host.Contracts;

public sealed record HostStopRequest(
    string? Reason);

public sealed record HostUnlockRequest(
    string? CredentialType);

public sealed record HostLockRequest(
    string? Reason);

public sealed record HostInitializeRequest(
    string? VaultPath);

public sealed record HostCreateNoteRequest(
    string? Title,
    string? Folder,
    string? Format,
    string? Content,
    IReadOnlyList<string>? Tags);

public sealed record HostAppendNoteRequest(
    int ExpectedRevision,
    string? Content);

public sealed record HostUpdateNoteRequest(
    int ExpectedRevision,
    string? Title,
    string? Folder,
    string? Content,
    IReadOnlyList<string>? Tags);

public sealed record HostTrashNoteRequest(
    int ExpectedRevision);

public sealed record HostRestoreTrashRequest(
    int ExpectedRevision,
    string? RenamePath);

public sealed record HostRestoreRevisionRequest(
    int ExpectedRevision);

public sealed record HostBackupRequest(
    string? DestinationPath);

public sealed record HostRestoreNewRequest(
    string? BackupPath,
    string? TargetPath);

public sealed record HostExportRequest(
    string? DestinationPath);

public sealed record HostImportPreviewRequest(
    string? SourceRoot);
