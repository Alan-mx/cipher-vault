using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;

namespace CipherVault.Host.Client;

public interface IHostVaultClient : IAsyncDisposable
{
    bool PromptsForCredentials { get; }

    Task<VaultInitializeResult> InitializeAsync(
        string vaultPath,
        string? typedPassword = null,
        CancellationToken cancellationToken = default);

    Task<VaultSessionStatus> UnlockAsync(
        string vaultPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default);

    Task<VaultSessionStatus> LockAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default);

    Task<VaultSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<VaultCatalogPage> ListCatalogAsync(
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VaultSearchPage> SearchNotesAsync(
        string query,
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VaultReadResult> ReadNoteAsync(
        Guid documentId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default);

    Task<VaultTrashPage> ListTrashAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VaultRestoreResult> RestoreTrashAsync(
        Guid documentId,
        int expectedRevision,
        string? renamePath,
        CancellationToken cancellationToken = default);

    Task<VaultPurgeResult> PurgeNoteAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    Task<VaultRevisionPage> ListRevisionsAsync(
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VaultWriteResult> RestoreRevisionAsync(
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        CancellationToken cancellationToken = default);

    Task<VaultImportPreview> PreviewImportAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default);

    Task<VaultImportResult> CommitImportAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<VaultExportResult> ExportSnapshotAsync(
        string destination,
        CancellationToken cancellationToken = default);

    Task<VaultBackupResult> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default);

    Task<VaultRestoreBackupResult> RestoreBackupAsync(
        string backupPath,
        string targetPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default);

    Task<VaultIntegrityReport> VerifyVaultAsync(
        CancellationToken cancellationToken = default);

    Task<int> RebuildSearchIndexAsync(
        CancellationToken cancellationToken = default);

    Task<VaultAuditPage> GetAuditPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task ChangePasswordAsync(
        string vaultPath,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);

    Task<string> RotateRecoveryKeyAsync(
        string vaultPath,
        string currentPassword,
        CancellationToken cancellationToken = default);

    Task ConfirmRecoveryKeyAsync(
        string vaultPath,
        string recoveryKey,
        CancellationToken cancellationToken = default);
}
