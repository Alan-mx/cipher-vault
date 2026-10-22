namespace CipherVault.Core.Backup;

public sealed record VaultBackupResult(
    string BackupPath,
    Guid VaultId,
    DateTimeOffset CreatedAt,
    long BackupBytes,
    bool AuditRecorded = true);

public sealed record VaultRestoreBackupResult(
    string VaultPath,
    Guid VaultId,
    bool AuditRecorded = true);

public sealed record VaultAuditItem(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string ActorKind,
    string? ClientName,
    string Action,
    Guid? DocumentId,
    string Result,
    int? BytesReturned,
    string DetailsJson);

public sealed record VaultAuditPage(
    IReadOnlyList<VaultAuditItem> Items,
    string? NextCursor);

public sealed record VaultIntegrityReport(
    bool Valid,
    int ActiveDocuments,
    int Revisions,
    int FtsRows,
    IReadOnlyList<string> Problems);
