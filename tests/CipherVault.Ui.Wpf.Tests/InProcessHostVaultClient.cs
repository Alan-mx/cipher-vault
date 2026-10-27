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
    private readonly bool _promptsForCredentials;

    public InProcessHostVaultClient(bool promptsForCredentials = false)
    {
        _promptsForCredentials = promptsForCredentials;
    }

    public bool PromptsForCredentials => _promptsForCredentials;
    public int LockCallCount { get; private set; }
    public string? LastLockReason { get; private set; }
    public int StopHostCallCount { get; private set; }
    public List<AdminAccessGrantListItem> AccessGrants { get; } = [];
    public Func<
        string?,
        string?,
        string?,
        int,
        CancellationToken,
        Task<AdminAccessGrantPage>>? ListAccessGrantsHandler
    { get; set; }

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

    public Task<VaultSearchPage> SearchNotesAsync(
        string query,
        string? folder,
        string? cursor,
        int limit,
        VaultSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        return _vault.SearchNotesAsync(
            query,
            folder,
            cursor,
            limit,
            filter,
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

    public Task<VaultUnlinkedMentionPage> ListUnlinkedMentionsAsync(
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _vault.ListUnlinkedMentionsAsync(
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
            VaultActorKind.UserUi,
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
            VaultActorKind.UserUi,
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
            VaultActorKind.UserUi,
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
            VaultActorKind.UserUi,
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
            VaultActorKind.UserUi,
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

    public Task<VaultLinkRebuildResult> RebuildLinkIndexAsync(
        CancellationToken cancellationToken = default)
    {
        return _vault.RebuildLinkIndexAsync(
            VaultActorKind.UserUi,
            cancellationToken);
    }

    public Task<VaultAuditPage> GetAuditPageAsync(
        VaultAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        return _vault.GetAuditPageAsync(query, cancellationToken);
    }

    public Task<AdminAccessGrantPage> ListAccessGrantsAsync(
        string? state,
        string? search,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        _ = cursor;
        _ = cancellationToken;
        if (ListAccessGrantsHandler is not null)
        {
            return ListAccessGrantsHandler(
                state,
                search,
                cursor,
                limit,
                cancellationToken);
        }
        var filteredSearch = AccessGrants
            .Where(item =>
                string.IsNullOrWhiteSpace(search) ||
                item.ClientId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.ClientDisplayName.Contains(
                    search,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var counts = new AdminAccessGrantStatusCounts(
            filteredSearch.Length,
            filteredSearch.Count(item => item.State == "active"),
            filteredSearch.Count(item => item.State == "revoked"),
            filteredSearch.Count(item => item.State == "expired"));
        var items = filteredSearch
            .Where(item =>
                string.IsNullOrWhiteSpace(state) ||
                string.Equals(item.State, state, StringComparison.Ordinal))
            .Take(limit)
            .ToArray();
        return Task.FromResult(
            new AdminAccessGrantPage(
                items,
                NextCursor: null,
                TotalCount: items.Length,
                counts));
    }

    public Task<AdminAccessGrantDetail> GetAccessGrantAsync(
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var item = AccessGrants.SingleOrDefault(grant => grant.GrantId == grantId)
            ?? throw new KeyNotFoundException("Access Grant was not found.");
        return Task.FromResult(
            new AdminAccessGrantDetail(
                item.GrantId,
                item.ClientId,
                item.ClientDisplayName,
                item.ClientInstanceId,
                item.State,
                item.Operations,
                item.Operations,
                item.ResourceScopes,
                item.CreatedAt,
                item.CreatedAt,
                item.HardExpiresAt,
                item.HardExpiresAt,
                item.LastUsedAt,
                null,
                null));
    }

    public Task<AdminAccessGrantRevokeResult> RevokeAccessGrantAsync(
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var index = AccessGrants.FindIndex(grant => grant.GrantId == grantId);
        if (index < 0)
        {
            throw new KeyNotFoundException("Access Grant was not found.");
        }
        var item = AccessGrants[index];
        var revoked = item with
        {
            State = "revoked",
            LastUsedAt = item.LastUsedAt
        };
        AccessGrants[index] = revoked;
        return Task.FromResult(
            new AdminAccessGrantRevokeResult(
                grantId,
                "revoked",
                DateTimeOffset.UtcNow,
                "admin-requested"));
    }

    public Task<AdminAccessGrantRevokeAllPreview>
        PreviewRevokeAllAccessGrantsAsync(
            CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var active = AccessGrants
            .Where(item => item.State == "active")
            .ToArray();
        var impacts = active
            .GroupBy(item => new
            {
                item.ClientId,
                item.ClientDisplayName
            })
            .Select(group => new AdminAccessGrantImpactGroup(
                group.Key.ClientId,
                group.Key.ClientDisplayName,
                group.Count(),
                0,
                group.SelectMany(item => item.Operations)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                group.SelectMany(item => item.ResourceScopes)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        return Task.FromResult(
            new AdminAccessGrantRevokeAllPreview(
                "test-preview",
                DateTimeOffset.UtcNow.AddMinutes(2),
                impacts,
                active.Length,
                0));
    }

    public Task<AdminAccessGrantRevokeAllResult>
        RevokeAllAccessGrantsAsync(
            string previewToken,
            CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (!string.Equals(previewToken, "test-preview", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Preview token is invalid.");
        }
        var revoked = 0;
        for (var index = 0; index < AccessGrants.Count; index++)
        {
            if (AccessGrants[index].State != "active")
            {
                continue;
            }
            AccessGrants[index] = AccessGrants[index] with
            {
                State = "revoked"
            };
            revoked++;
        }
        return Task.FromResult(
            new AdminAccessGrantRevokeAllResult(
                revoked,
                0,
                0,
                0));
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

    public Task StopHostAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        StopHostCallCount++;
        return Task.CompletedTask;
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
