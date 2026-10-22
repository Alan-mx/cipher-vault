using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using System.Text.Json;

namespace CipherVault.Host.Client;

public sealed class NamedPipeHostVaultClient : IHostVaultClient
{
    private readonly AdminPipeClientOptions _options;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private AdminPipeClientConnection? _connection;
    private bool _disposed;

    private NamedPipeHostVaultClient(AdminPipeClientOptions options)
    {
        _options = options;
    }

    public bool PromptsForCredentials => true;

    public static Task<NamedPipeHostVaultClient> CreateAsync(
        AdminPipeClientOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NamedPipeHostVaultClient(options));
    }

    public async Task<VaultInitializeResult> InitializeAsync(
        string vaultPath,
        string? typedPassword = null,
        CancellationToken cancellationToken = default)
    {
        var data = await CallAsync<HostInitializeData>(
            "admin.vault.initialize",
            new HostInitializeRequest(vaultPath),
            cancellationToken).ConfigureAwait(false);
        return new VaultInitializeResult(
            data.VaultId,
            data.VaultPath,
            data.RecoveryKey,
            data.CreatedAt);
    }

    public async Task<VaultSessionStatus> UnlockAsync(
        string vaultPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        var data = await CallAsync<HostSessionStatusData>(
            "admin.session.unlock",
            new HostUnlockRequest(ToCredentialPreference(credentialType)),
            cancellationToken).ConfigureAwait(false);
        return ToSessionStatus(data);
    }

    public async Task<VaultSessionStatus> LockAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        var data = await CallAsync<HostSessionStatusData>(
            "admin.session.lock",
            new HostLockRequest(reason),
            cancellationToken).ConfigureAwait(false);
        return ToSessionStatus(data);
    }

    public async Task<VaultSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await CallAsync<AdminPipeStatusData>(
            "admin.status",
            new { },
            cancellationToken).ConfigureAwait(false);
        return ToSessionStatus(
            data.Session,
            data.BrokerAvailable);
    }

    public Task<VaultCatalogPage> ListCatalogAsync(
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultCatalogPage>(
            "admin.catalog.list",
            new { folder, cursor, limit },
            cancellationToken);
    }

    public Task<VaultSearchPage> SearchNotesAsync(
        string query,
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultSearchPage>(
            "admin.search.notes",
            new { query, folder, cursor, limit },
            cancellationToken);
    }

    public Task<VaultReadResult> ReadNoteAsync(
        Guid documentId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultReadResult>(
            "admin.notes.read",
            new { documentId, offset, maxChars },
            cancellationToken);
    }

    public Task<VaultScanPage> ScanNotesAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultScanPage>(
            "admin.notes.scan",
            new { cursor, limit },
            cancellationToken);
    }

    public Task<VaultWriteResult> CreateNoteAsync(
        string title,
        string? folder,
        string format,
        string content,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultWriteResult>(
            "admin.notes.create",
            new HostCreateNoteRequest(title, folder, format, content, tags),
            cancellationToken);
    }

    public Task<VaultWriteResult> AppendNoteAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultWriteResult>(
            "admin.notes.append",
            new
            {
                documentId,
                request = new HostAppendNoteRequest(expectedRevision, content)
            },
            cancellationToken);
    }

    public Task<VaultWriteResult> UpdateNoteAsync(
        Guid documentId,
        int expectedRevision,
        VaultNotePatch patch,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultWriteResult>(
            "admin.notes.update",
            new
            {
                documentId,
                request = new HostUpdateNoteRequest(
                    expectedRevision,
                    patch.Title,
                    patch.Folder,
                    patch.Content,
                    patch.Tags)
            },
            cancellationToken);
    }

    public Task<VaultDeleteResult> DeleteNoteAsync(
        Guid documentId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultDeleteResult>(
            "admin.notes.delete",
            new
            {
                documentId,
                request = new HostTrashNoteRequest(expectedRevision)
            },
            cancellationToken);
    }

    public Task<VaultTrashPage> ListTrashAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultTrashPage>(
            "admin.trash.list",
            new { cursor, limit },
            cancellationToken);
    }

    public Task<VaultRestoreResult> RestoreTrashAsync(
        Guid documentId,
        int expectedRevision,
        string? renamePath,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultRestoreResult>(
            "admin.trash.restore",
            new
            {
                documentId,
                request = new HostRestoreTrashRequest(expectedRevision, renamePath)
            },
            cancellationToken);
    }

    public Task<VaultPurgeResult> PurgeNoteAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultPurgeResult>(
            "admin.trash.purge",
            new { documentId },
            cancellationToken);
    }

    public Task<VaultRevisionPage> ListRevisionsAsync(
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultRevisionPage>(
            "admin.revisions.list",
            new { documentId, cursor, limit },
            cancellationToken);
    }

    public Task<VaultWriteResult> RestoreRevisionAsync(
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultWriteResult>(
            "admin.revisions.restore",
            new
            {
                documentId,
                revisionId,
                request = new HostRestoreRevisionRequest(expectedRevision)
            },
            cancellationToken);
    }

    public Task<VaultImportPreview> PreviewImportAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultImportPreview>(
            "admin.maintenance.import_preview",
            new HostImportPreviewRequest(sourceRoot),
            cancellationToken);
    }

    public Task<VaultImportResult> CommitImportAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultImportResult>(
            "admin.maintenance.import_commit",
            new { jobId },
            cancellationToken);
    }

    public Task<VaultExportResult> ExportSnapshotAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultExportResult>(
            "admin.maintenance.export",
            new HostExportRequest(destination),
            cancellationToken);
    }

    public Task<VaultBackupResult> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultBackupResult>(
            "admin.maintenance.backup",
            new HostBackupRequest(backupPath),
            cancellationToken);
    }

    public Task<VaultRestoreBackupResult> RestoreBackupAsync(
        string backupPath,
        string targetPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultRestoreBackupResult>(
            "admin.maintenance.restore_new",
            new HostRestoreNewRequest(backupPath, targetPath),
            cancellationToken);
    }

    public Task<VaultIntegrityReport> VerifyVaultAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultIntegrityReport>(
            "admin.maintenance.verify",
            new { },
            cancellationToken);
    }

    public Task<int> RebuildSearchIndexAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<int>(
            "admin.maintenance.rebuild_index",
            new { },
            cancellationToken);
    }

    public Task<VaultAuditPage> GetAuditPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<VaultAuditPage>(
            "admin.maintenance.audit",
            new { cursor, limit },
            cancellationToken);
    }

    public async Task ChangePasswordAsync(
        string vaultPath,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        _ = await CallAsync<JsonElement>(
            "admin.maintenance.change_password",
            new { },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RotateRecoveryKeyAsync(
        string vaultPath,
        string currentPassword,
        CancellationToken cancellationToken = default)
    {
        var data = await CallAsync<HostRecoveryRotationData>(
            "admin.maintenance.rotate_recovery",
            new { },
            cancellationToken).ConfigureAwait(false);
        return data.RecoveryKey;
    }

    public async Task ConfirmRecoveryKeyAsync(
        string vaultPath,
        string recoveryKey,
        CancellationToken cancellationToken = default)
    {
        _ = await CallAsync<JsonElement>(
            "admin.maintenance.confirm_recovery",
            new { },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<HostClientData>> ListAgentClientsAsync(
        CancellationToken cancellationToken = default)
    {
        return CallAsync<IReadOnlyList<HostClientData>>(
            "admin.agent.clients.list",
            new { },
            cancellationToken);
    }

    public Task<HostCreateClientData> CreateAgentClientAsync(
        string clientId,
        string accessMode,
        string authorizationMode,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<HostCreateClientData>(
            "admin.agent.clients.create",
            new HostCreateClientRequest(clientId, accessMode, authorizationMode),
            cancellationToken);
    }

    public Task<HostCreateClientData> RotateAgentClientAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        return CallAsync<HostCreateClientData>(
            "admin.agent.clients.rotate",
            new { clientId },
            cancellationToken);
    }

    public async Task DeleteAgentClientAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        _ = await CallAsync<JsonElement>(
            "admin.agent.clients.delete",
            new { clientId },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopHostAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        _ = await CallAsync<JsonElement>(
            "admin.service.stop",
            new HostStopRequest(reason),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        _connectGate.Dispose();
    }

    private async Task<T> CallAsync<T>(
        string method,
        object payload,
        CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.CallAsync<object, T>(
            method,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdminPipeClientConnection> GetConnectionAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_connection is not null)
        {
            return _connection;
        }
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection ??= await AdminPipeClientConnection.ConnectAsync(
                _options,
                cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static VaultSessionStatus ToSessionStatus(HostSessionStatusData status)
    {
        return ToSessionStatus(status, brokerAvailable: false);
    }

    private static VaultSessionStatus ToSessionStatus(
        HostSessionStatusData status,
        bool brokerAvailable)
    {
        return new VaultSessionStatus(
            Enum.Parse<VaultSessionState>(status.State, ignoreCase: true),
            status.SessionId,
            status.StartedAt,
            status.LastUserActivityAt,
            status.IdleExpiresAt,
            status.HardExpiresAt,
            status.LockReason,
            status.RecoveryVerified,
            brokerAvailable);
    }

    private static string ToCredentialPreference(VaultCredentialType credentialType)
    {
        return credentialType switch
        {
            VaultCredentialType.Password => "password",
            VaultCredentialType.RecoveryKey => "recoveryKey",
            _ => "any"
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
