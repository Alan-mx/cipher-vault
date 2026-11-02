using CipherVault.Core.Backup;

namespace CipherVault.Application.Backup;

public sealed record BackupSnapshotCapture(
    Guid VaultId,
    DateTimeOffset CapturedAtUtc,
    string WorkingDirectory,
    string DatabasePath,
    string KeyEnvelopePath,
    long DatabaseBytes,
    long KeyEnvelopeBytes);

public sealed record BackupCreateResult(
    BackupListItem Item,
    bool AuditRecorded);
