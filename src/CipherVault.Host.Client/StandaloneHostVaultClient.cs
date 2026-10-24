using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Client;

public sealed class StandaloneHostVaultClient : IHostVaultClient
{
    private readonly VaultService _vault;
    private readonly VaultActorKind _actor;

    public StandaloneHostVaultClient(
        VaultService vault,
        VaultActorKind actor = VaultActorKind.UserCli)
    {
        _vault = vault;
        _actor = actor;
    }

    public bool PromptsForCredentials => false;

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

    public Task<VaultDocumentMap> GetDocumentMapAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return _vault.GetDocumentMapAsync(documentId, cancellationToken);
    }

    public Task<VaultBacklinkPage> ListBacklinksAsync(
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.ListBacklinksAsync(
            documentId,
            cursor,
            limit,
            cancellationToken);
    }

    public Task<VaultTagPage> ListTagsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.ListTagsAsync(cursor, limit, cancellationToken);
    }

    public Task<VaultLocalGraphResult> GetLocalGraphAsync(
        VaultLocalGraphQuery query,
        CancellationToken cancellationToken = default)
    {
        return _vault.GetLocalGraphAsync(query, cancellationToken);
    }

    public Task<VaultWriteResult> CreateNoteAsync(
        string title,
        string? folder,
        string format,
        string content,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default)
    {
        return _vault.CreateNoteAsync(
            title,
            folder,
            format,
            content,
            tags,
            _actor,
            cancellationToken);
    }

    public Task<VaultWriteResult> AppendNoteAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        CancellationToken cancellationToken = default)
    {
        return _vault.AppendNoteAsync(
            documentId,
            expectedRevision,
            content,
            _actor,
            cancellationToken);
    }

    public Task<VaultWriteResult> UpdateNoteAsync(
        Guid documentId,
        int expectedRevision,
        VaultNotePatch patch,
        CancellationToken cancellationToken = default)
    {
        return _vault.UpdateNoteAsync(
            documentId,
            expectedRevision,
            patch,
            _actor,
            cancellationToken);
    }

    public Task<VaultPatchResult> PatchNoteAsync(
        Guid documentId,
        int expectedRevision,
        VaultNotePatchCommand command,
        CancellationToken cancellationToken = default)
    {
        return _vault.PatchNoteAsync(
            documentId,
            expectedRevision,
            command,
            _actor,
            cancellationToken);
    }

    public Task<VaultDeleteResult> DeleteNoteAsync(
        Guid documentId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return _vault.DeleteNoteAsync(
            documentId,
            expectedRevision,
            _actor,
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
            _actor,
            cancellationToken);
    }

    public Task<VaultPurgeResult> PurgeNoteAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return _vault.PurgeNoteAsync(documentId, _actor, cancellationToken);
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
            _actor,
            cancellationToken);
    }

    public Task<VaultImportPreview> PreviewImportAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        return _vault.PreviewImportAsync(
            sourceRoot,
            _actor,
            cancellationToken);
    }

    public Task<VaultImportResult> CommitImportAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return _vault.CommitImportAsync(jobId, _actor, cancellationToken);
    }

    public Task<VaultExportResult> ExportSnapshotAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        return _vault.ExportSnapshotAsync(
            destination,
            _actor,
            cancellationToken);
    }

    public Task<VaultBackupResult> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        return _vault.CreateBackupAsync(backupPath, _actor, cancellationToken);
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
            _actor,
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
        return _vault.RebuildSearchIndexAsync(_actor, cancellationToken);
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
            _actor,
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
            _actor,
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
            _actor,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _vault.DisposeAsync();
    }

    public Task StopHostAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        throw HostManagementRequired();
    }

    private static VaultException HostManagementRequired()
    {
        return new VaultException(
            VaultErrorCode.InvalidInput,
            "Agent client management requires an online Host.");
    }
}
