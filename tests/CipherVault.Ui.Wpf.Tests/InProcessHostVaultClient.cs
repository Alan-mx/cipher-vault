using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;

namespace CipherVault.Ui.Wpf.Tests;

internal sealed class InProcessHostVaultClient : IHostVaultClient
{
    private readonly VaultService _vault = new(
        new AlwaysActiveActivityProvider());

    public bool PromptsForCredentials => false;
    public int LockCallCount { get; private set; }
    public string? LastLockReason { get; private set; }

    public Task<VaultInitializeResult> InitializeAsync(
        string vaultPath,
        string? typedPassword = null,
        CancellationToken cancellationToken = default)
    {
        return _vault.InitializeAsync(
            vaultPath,
            typedPassword ?? string.Empty,
            cancellationToken);
    }

    public Task<VaultSessionStatus> UnlockAsync(
        string vaultPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        return _vault.UnlockAsync(
            vaultPath,
            credential,
            credentialType,
            cancellationToken);
    }

    public Task<VaultSessionStatus> LockAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        LockCallCount++;
        LastLockReason = reason;
        return _vault.LockAsync(reason, cancellationToken);
    }

    public Task<VaultSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        return _vault.GetStatusAsync(cancellationToken);
    }

    public Task<VaultCatalogPage> ListCatalogAsync(
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.ListCatalogAsync(folder, cursor, limit, cancellationToken);
    }

    public Task<VaultSearchPage> SearchNotesAsync(
        string query,
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.SearchNotesAsync(
            query,
            folder,
            cursor,
            limit,
            cancellationToken);
    }

    public Task<VaultReadResult> ReadNoteAsync(
        Guid documentId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        return _vault.ReadNoteAsync(
            documentId,
            offset,
            maxChars,
            cancellationToken);
    }

    public Task<VaultTrashPage> ListTrashAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.ListTrashAsync(cursor, limit, cancellationToken);
    }

    public Task<VaultRestoreResult> RestoreTrashAsync(
        Guid documentId,
        int expectedRevision,
        string? renamePath,
        CancellationToken cancellationToken = default)
    {
        return _vault.RestoreTrashAsync(
            documentId,
            expectedRevision,
            renamePath,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultPurgeResult> PurgeNoteAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return _vault.PurgeNoteAsync(
            documentId,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultRevisionPage> ListRevisionsAsync(
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.ListRevisionsAsync(
            documentId,
            cursor,
            limit,
            cancellationToken);
    }

    public Task<VaultWriteResult> RestoreRevisionAsync(
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return _vault.RestoreRevisionAsync(
            documentId,
            revisionId,
            expectedRevision,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultImportPreview> PreviewImportAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        return _vault.PreviewImportAsync(
            sourceRoot,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultImportResult> CommitImportAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return _vault.CommitImportAsync(
            jobId,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultExportResult> ExportSnapshotAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        return _vault.ExportSnapshotAsync(
            destination,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultBackupResult> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        return _vault.CreateBackupAsync(
            backupPath,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultRestoreBackupResult> RestoreBackupAsync(
        string backupPath,
        string targetPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        return _vault.RestoreBackupAsync(
            backupPath,
            targetPath,
            credential,
            credentialType,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultIntegrityReport> VerifyVaultAsync(
        CancellationToken cancellationToken = default)
    {
        return _vault.VerifyVaultAsync(cancellationToken);
    }

    public Task<int> RebuildSearchIndexAsync(
        CancellationToken cancellationToken = default)
    {
        return _vault.RebuildSearchIndexAsync(
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultAuditPage> GetAuditPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.GetAuditPageAsync(cursor, limit, cancellationToken);
    }

    public Task ChangePasswordAsync(
        string vaultPath,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        return _vault.ChangePasswordAsync(
            vaultPath,
            currentPassword,
            newPassword,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<string> RotateRecoveryKeyAsync(
        string vaultPath,
        string currentPassword,
        CancellationToken cancellationToken = default)
    {
        return _vault.RotateRecoveryKeyAsync(
            vaultPath,
            currentPassword,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task ConfirmRecoveryKeyAsync(
        string vaultPath,
        string recoveryKey,
        CancellationToken cancellationToken = default)
    {
        return _vault.ConfirmRecoveryKeyAsync(
            vaultPath,
            recoveryKey,
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _vault.DisposeAsync();
    }

    public Task<IReadOnlyList<HostClientData>> ListAgentClientsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<HostClientData>>([]);
    }

    public Task<HostCreateClientData> CreateAgentClientAsync(
        string clientId,
        string accessMode,
        bool allowLock,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<HostCreateClientData> RotateAgentClientAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task DeleteAgentClientAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
