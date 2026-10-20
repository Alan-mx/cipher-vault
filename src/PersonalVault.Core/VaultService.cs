using System.Security.Cryptography;
using PersonalVault.Core.Backup;
using PersonalVault.Core.Crypto;
using PersonalVault.Core.Documents;
using PersonalVault.Core.Errors;
using PersonalVault.Core.ImportExport;
using PersonalVault.Core.Security;
using PersonalVault.Core.Session;
using PersonalVault.Core.Storage;

namespace PersonalVault.Core;

public interface IVaultService : IAsyncDisposable
{
    Task<VaultInitializeResult> InitializeAsync(
        string vaultPath,
        string password,
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

    Task<VaultScanPage> ScanNotesAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VaultWriteResult> CreateNoteAsync(
        string title,
        string? folder,
        string format,
        string content,
        IReadOnlyList<string>? tags,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default);

    Task<VaultWriteResult> AppendNoteAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default);

    Task<VaultWriteResult> UpdateNoteAsync(
        Guid documentId,
        int expectedRevision,
        VaultNotePatch patch,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default);

    Task<VaultDeleteResult> DeleteNoteAsync(
        Guid documentId,
        int expectedRevision,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default);

    Task<VaultTrashPage> ListTrashAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VaultRestoreResult> RestoreTrashAsync(
        Guid documentId,
        int expectedRevision,
        string? renamePath,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<VaultPurgeResult> PurgeNoteAsync(
        Guid documentId,
        VaultActorKind actor = VaultActorKind.UserUi,
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
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<VaultBackupResult> CreateBackupAsync(
        string backupPath,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<VaultRestoreBackupResult> RestoreBackupAsync(
        string backupPath,
        string targetPath,
        string credential,
        VaultCredentialType credentialType,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<VaultAuditPage> GetAuditPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VaultIntegrityReport> VerifyVaultAsync(
        CancellationToken cancellationToken = default);

    Task<int> RebuildSearchIndexAsync(
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task ChangePasswordAsync(
        string vaultPath,
        string currentPassword,
        string newPassword,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<string> RotateRecoveryKeyAsync(
        string vaultPath,
        string currentPassword,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task ConfirmRecoveryKeyAsync(
        string vaultPath,
        string recoveryKey,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<VaultImportPreview> PreviewImportAsync(
        string sourceRoot,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<VaultImportResult> CommitImportAsync(
        Guid jobId,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);

    Task<VaultExportResult> ExportSnapshotAsync(
        string destination,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default);
}

public sealed class VaultService : IVaultService
{
    private const string PendingKeyFileName = "vault.keys.pending";
    private readonly ISystemActivityProvider _activityProvider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VaultSession? _session;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _disposed;

    public VaultService(
        ISystemActivityProvider activityProvider,
        TimeProvider? timeProvider = null)
    {
        _activityProvider = activityProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        SqlCipherVaultStore.InitializeRuntime();
    }

    public async Task<VaultInitializeResult> InitializeAsync(
        string vaultPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        VaultKeyring.ValidatePassword(password);
        var fullPath = NormalizeVaultPath(vaultPath);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new VaultException(
                VaultErrorCode.AlreadyExists,
                "Vault path already exists. Initialization requires a new directory.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        VaultProcessLock? processLock = null;
        var temporaryPath = string.Empty;
        try
        {
            processLock = await VaultProcessLock.AcquireAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            var parent = Path.GetDirectoryName(fullPath)
                ?? throw new VaultException(VaultErrorCode.InvalidInput, "Vault path has no parent.");
            Directory.CreateDirectory(parent);
            temporaryPath = Path.Combine(
                parent,
                $".{Path.GetFileName(fullPath)}.init-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryPath);
            VaultFileSecurity.ProtectDirectory(temporaryPath);

            var vaultId = Guid.NewGuid();
            var creation = await VaultKeyring.CreateAsync(password, vaultId, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await VaultKeyring.WriteEnvelopeAtomicAsync(
                    Path.Combine(temporaryPath, "vault.keys"),
                    creation.Envelope,
                    cancellationToken).ConfigureAwait(false);

                var material = new UnlockedKeyMaterial(vaultId, creation.MasterKey, creation.DbKey);
                await SqlCipherVaultStore.InitializeAsync(temporaryPath, material, cancellationToken)
                    .ConfigureAwait(false);
                await SqlCipherVaultStore.VerifyAsync(temporaryPath, material, cancellationToken)
                    .ConfigureAwait(false);

                var reloadedEnvelope = await VaultKeyring.LoadEnvelopeAsync(
                    Path.Combine(temporaryPath, "vault.keys"),
                    cancellationToken).ConfigureAwait(false);
                var recovered = await VaultKeyring.UnwrapAsync(
                    reloadedEnvelope,
                    creation.RecoveryKey,
                    VaultCredentialType.RecoveryKey,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(recovered.MasterKey, creation.MasterKey))
                    {
                        throw new VaultException(
                            VaultErrorCode.CorruptVault,
                            "Recovery key verification failed.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(recovered.MasterKey);
                    CryptographicOperations.ZeroMemory(recovered.DbKey);
                }

                Directory.Move(temporaryPath, fullPath);
                VaultFileSecurity.ProtectDirectory(fullPath);
                temporaryPath = string.Empty;
                return new VaultInitializeResult(
                    vaultId,
                    fullPath,
                    creation.RecoveryKey,
                    creation.Envelope.CreatedAt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(creation.MasterKey);
                CryptographicOperations.ZeroMemory(creation.DbKey);
            }
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault initialization failed.",
                exception);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath) && Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }
            processLock?.Dispose();
            _gate.Release();
        }
    }

    public async Task<VaultSessionStatus> UnlockAsync(
        string vaultPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fullPath = NormalizeVaultPath(vaultPath);
        var keyPath = Path.Combine(fullPath, "vault.keys");
        if (!Directory.Exists(fullPath) || !File.Exists(keyPath))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Vault directory or key file was not found.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        VaultProcessLock? processLock = null;
        try
        {
            processLock = await VaultProcessLock.AcquireAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            await LockCoreAsync("replaced-by-new-unlock", cancellationToken).ConfigureAwait(false);
            VaultFileSecurity.ProtectDirectory(fullPath);
            var envelope = await VaultKeyring.LoadEnvelopeAsync(keyPath, cancellationToken)
                .ConfigureAwait(false);
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                credential,
                credentialType,
                cancellationToken).ConfigureAwait(false);

            var integrityKey = VaultKeyring.DeriveIntegrityKey(material.MasterKey);
            try
            {
                var connection = await SqlCipherVaultStore.OpenAsync(
                    fullPath,
                    material,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    var recoveryVerified = await SqlCipherVaultStore.IsRecoveryVerifiedAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                    var now = _timeProvider.GetUtcNow();
                    var session = new VaultSession
                    {
                        VaultPath = fullPath,
                        VaultId = material.VaultId,
                        Connection = connection,
                        MasterKey = material.MasterKey,
                        DbKey = material.DbKey,
                        IntegrityKey = integrityKey,
                        StartedAt = now,
                        LastUserActivityAt = now,
                        HardExpiresAt = now + VaultSession.HardLifetime,
                        RecoveryVerified = recoveryVerified
                    };
                    await DocumentStore.CleanupExpiredAsync(
                        connection,
                        session.SessionId,
                        now,
                        cancellationToken).ConfigureAwait(false);
                    material = material with
                    {
                        MasterKey = Array.Empty<byte>(),
                        DbKey = Array.Empty<byte>()
                    };
                    integrityKey = Array.Empty<byte>();
                    _session = session;
                    StartMonitor();
                    return CreateStatus(session, null);
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
                CryptographicOperations.ZeroMemory(integrityKey);
            }
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault unlock failed.",
                exception);
        }
        finally
        {
            processLock?.Dispose();
            _gate.Release();
        }
    }

    public async Task<VaultSessionStatus> LockAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LockCoreAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
            {
                return GetLockedStatus(null);
            }

            RefreshActivity(_session);
            if (IsExpired(_session))
            {
                return await LockCoreAsync("session-expired", cancellationToken)
                    .ConfigureAwait(false);
            }

            SetExpiringState(_session);
            return CreateStatus(_session, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultCatalogPage> ListCatalogAsync(
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var page = await DocumentStore.ListCatalogAsync(
                session.Connection,
                session.SessionId,
                folder,
                cursor,
                limit,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return page;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultSearchPage> SearchNotesAsync(
        string query,
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var page = await DocumentStore.SearchAsync(
                session.Connection,
                session.SessionId,
                query,
                folder,
                cursor,
                limit,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return page;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultReadResult> ReadNoteAsync(
        Guid documentId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentStore.ReadNoteAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                documentId,
                offset,
                maxChars,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultScanPage> ScanNotesAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentStore.ScanAsync(
                session.Connection,
                session.SessionId,
                cursor,
                limit,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultWriteResult> CreateNoteAsync(
        string title,
        string? folder,
        string format,
        string content,
        IReadOnlyList<string>? tags,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var documentId = Guid.NewGuid();
            var result = await DocumentWriter.CreateAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                documentId,
                title,
                folder,
                format,
                content,
                tags,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultWriteResult> AppendNoteAsync(
        Guid documentId,
        int expectedRevision,
        string content,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentWriter.AppendAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                documentId,
                expectedRevision,
                content,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultWriteResult> UpdateNoteAsync(
        Guid documentId,
        int expectedRevision,
        VaultNotePatch patch,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentWriter.UpdateAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                documentId,
                expectedRevision,
                patch,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultDeleteResult> DeleteNoteAsync(
        Guid documentId,
        int expectedRevision,
        VaultActorKind actor = VaultActorKind.Mcp,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentWriter.SoftDeleteAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                documentId,
                expectedRevision,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultTrashPage> ListTrashAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var page = await DocumentStore.ListTrashAsync(
                session.Connection,
                session.SessionId,
                cursor,
                limit,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return page;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultRestoreResult> RestoreTrashAsync(
        Guid documentId,
        int expectedRevision,
        string? renamePath,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentWriter.RestoreTrashAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                documentId,
                expectedRevision,
                renamePath,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultPurgeResult> PurgeNoteAsync(
        Guid documentId,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentWriter.PurgeAsync(
                session.Connection,
                session.SessionId,
                documentId,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultRevisionPage> ListRevisionsAsync(
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var page = await DocumentStore.ListRevisionsAsync(
                session.Connection,
                session.SessionId,
                documentId,
                cursor,
                limit,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return page;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultWriteResult> RestoreRevisionAsync(
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await DocumentWriter.RestoreRevisionAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                documentId,
                revisionId,
                expectedRevision,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultBackupResult> CreateBackupAsync(
        string backupPath,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        var fullPath = NormalizeVaultPath(backupPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        VaultProcessLock? processLock = null;
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            processLock = await VaultProcessLock.AcquireAsync(
                session.VaultPath,
                cancellationToken).ConfigureAwait(false);
            var result = await BackupService.CreateAsync(
                session,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            processLock?.Dispose();
            _gate.Release();
        }
    }

    public async Task<VaultRestoreBackupResult> RestoreBackupAsync(
        string backupPath,
        string targetPath,
        string credential,
        VaultCredentialType credentialType,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await BackupService.RestoreAsync(
                backupPath,
                targetPath,
                credential,
                credentialType,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultAuditPage> GetAuditPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var page = await DocumentStore.ListAuditAsync(
                session.Connection,
                session.SessionId,
                cursor,
                limit,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return page;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultIntegrityReport> VerifyVaultAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var report = await DocumentStore.VerifyAsync(
                session.Connection,
                session.IntegrityKey,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RebuildSearchIndexAsync(
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var rebuilt = await DocumentStore.RebuildFtsAsync(
                session.Connection,
                session.SessionId,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return rebuilt;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ChangePasswordAsync(
        string vaultPath,
        string currentPassword,
        string newPassword,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        VaultKeyring.ValidatePassword(newPassword);
        var fullPath = NormalizeVaultPath(vaultPath);
        var keyPath = Path.Combine(fullPath, "vault.keys");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        VaultProcessLock? processLock = null;
        string? backupPath = null;
        try
        {
            processLock = await VaultProcessLock.AcquireAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            VaultFileSecurity.ProtectDirectory(fullPath);
            var envelope = await VaultKeyring.LoadEnvelopeAsync(keyPath, cancellationToken)
                .ConfigureAwait(false);
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                currentPassword,
                VaultCredentialType.Password,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var updatedEnvelope = envelope with
                {
                    KeyGeneration = envelope.KeyGeneration + 1,
                    PasswordSlot = VaultKeyring.CreatePasswordSlot(
                        material.MasterKey,
                        newPassword,
                        envelope.VaultId)
                };
                backupPath = await VaultKeyring.WriteEnvelopeWithBackupAsync(
                    keyPath,
                    updatedEnvelope,
                    cancellationToken).ConfigureAwait(false);
                await VerifyEnvelopeAndDatabaseAsync(
                    fullPath,
                    updatedEnvelope,
                    newPassword,
                    VaultCredentialType.Password,
                    cancellationToken).ConfigureAwait(false);
                await AppendMaintenanceAuditAsync(
                    fullPath,
                    material,
                    actor,
                    "change_password",
                    cancellationToken).ConfigureAwait(false);
                DeleteIfExists(backupPath);
            }
            catch
            {
                RestoreKeyBackup(keyPath, backupPath);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }
        finally
        {
            processLock?.Dispose();
            _gate.Release();
        }
    }

    public async Task<string> RotateRecoveryKeyAsync(
        string vaultPath,
        string currentPassword,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        var fullPath = NormalizeVaultPath(vaultPath);
        var keyPath = Path.Combine(fullPath, "vault.keys");
        var pendingKeyPath = Path.Combine(fullPath, PendingKeyFileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        VaultProcessLock? processLock = null;
        try
        {
            processLock = await VaultProcessLock.AcquireAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            VaultFileSecurity.ProtectDirectory(fullPath);
            var envelope = await VaultKeyring.LoadEnvelopeAsync(keyPath, cancellationToken)
                .ConfigureAwait(false);
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                currentPassword,
                VaultCredentialType.Password,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var (newRecoveryKey, slot) = VaultKeyring.CreateRecoverySlot(
                    material.MasterKey,
                    envelope.VaultId);
                var updatedEnvelope = envelope with
                {
                    KeyGeneration = envelope.KeyGeneration + 1,
                    RecoverySlot = slot
                };
                await VaultKeyring.WriteEnvelopeAtomicAsync(
                    pendingKeyPath,
                    updatedEnvelope,
                    cancellationToken).ConfigureAwait(false);
                await VerifyEnvelopeAndDatabaseAsync(
                    fullPath,
                    updatedEnvelope,
                    newRecoveryKey,
                    VaultCredentialType.RecoveryKey,
                    cancellationToken,
                    PendingKeyFileName).ConfigureAwait(false);
                return newRecoveryKey;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }
        finally
        {
            processLock?.Dispose();
            _gate.Release();
        }
    }

    public async Task ConfirmRecoveryKeyAsync(
        string vaultPath,
        string recoveryKey,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        var fullPath = NormalizeVaultPath(vaultPath);
        var activeKeyPath = Path.Combine(fullPath, "vault.keys");
        var pendingKeyPath = Path.Combine(fullPath, PendingKeyFileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        VaultProcessLock? processLock = null;
        string? backupPath = null;
        try
        {
            processLock = await VaultProcessLock.AcquireAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            VaultFileSecurity.ProtectDirectory(fullPath);
            var hasPendingRotation = File.Exists(pendingKeyPath);
            var keyPath = hasPendingRotation ? pendingKeyPath : activeKeyPath;
            var envelope = await VaultKeyring.LoadEnvelopeAsync(keyPath, cancellationToken)
                .ConfigureAwait(false);
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                recoveryKey,
                VaultCredentialType.RecoveryKey,
                cancellationToken).ConfigureAwait(false);
            var rotationActivated = false;
            try
            {
                if (hasPendingRotation)
                {
                    backupPath = await VaultKeyring.WriteEnvelopeWithBackupAsync(
                        activeKeyPath,
                        envelope,
                        cancellationToken).ConfigureAwait(false);
                    await VerifyEnvelopeAndDatabaseAsync(
                        fullPath,
                        envelope,
                        recoveryKey,
                        VaultCredentialType.RecoveryKey,
                        cancellationToken).ConfigureAwait(false);
                }

                await MarkRecoveryVerifiedAndAuditAsync(
                    fullPath,
                    material,
                    actor,
                    hasPendingRotation ? "rotate_recovery" : "confirm_recovery",
                    cancellationToken).ConfigureAwait(false);

                if (_session is not null &&
                    string.Equals(_session.VaultPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _session.RecoveryVerified = true;
                }
                rotationActivated = true;

                if (hasPendingRotation)
                {
                    TryDeleteIfExists(pendingKeyPath);
                    TryDeleteIfExists(backupPath);
                }
            }
            catch
            {
                if (hasPendingRotation && !rotationActivated)
                {
                    RestoreKeyBackup(activeKeyPath, backupPath);
                }
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }
        finally
        {
            processLock?.Dispose();
            _gate.Release();
        }
    }

    public async Task<VaultImportPreview> PreviewImportAsync(
        string sourceRoot,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var preview = await ImportExportService.PreviewImportAsync(
                session.VaultPath,
                session.Connection,
                session.SessionId,
                sourceRoot,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return preview;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultImportResult> CommitImportAsync(
        Guid jobId,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await ImportExportService.CommitImportAsync(
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                jobId,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultExportResult> ExportSnapshotAsync(
        string destination,
        VaultActorKind actor = VaultActorKind.UserUi,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireUserActor(actor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await ImportExportService.ExportAsync(
                session.VaultPath,
                session.Connection,
                session.SessionId,
                session.IntegrityKey,
                destination,
                actor,
                cancellationToken).ConfigureAwait(false);
            await EnsureSessionStillValidAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await LockAsync("disposed").ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void StartMonitor()
    {
        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorAsync(_monitorCts.Token));
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_session is null)
                    {
                        return;
                    }

                    RefreshActivity(_session);
                    if (IsExpired(_session))
                    {
                        await LockCoreAsync("session-expired", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    SetExpiringState(_session);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await LockCoreAsync(
                    "session-monitor-failed",
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task<VaultSessionStatus> LockCoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var session = _session;
        _session = null;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _monitorTask = null;

        if (session is null)
        {
            return GetLockedStatus(reason);
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        return GetLockedStatus(reason);
    }

    private void RefreshActivity(VaultSession session)
    {
        var now = _timeProvider.GetUtcNow();
        var snapshot = _activityProvider.GetSnapshot();
        if (snapshot.IsWorkstationLocked)
        {
            return;
        }

        var latestActivity = now - snapshot.SystemIdleTime;
        if (latestActivity > session.LastUserActivityAt)
        {
            session.LastUserActivityAt = latestActivity;
        }
    }

    private bool IsExpired(VaultSession session)
    {
        var snapshot = _activityProvider.GetSnapshot();
        if (snapshot.IsWorkstationLocked)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        return now >= session.HardExpiresAt ||
            now - session.LastUserActivityAt >= VaultSession.IdleTimeout;
    }

    private async Task<VaultSession> RequireSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            throw new VaultException(VaultErrorCode.SessionLocked, "Vault session is locked.");
        }

        RefreshActivity(_session);
        if (IsExpired(_session))
        {
            await LockCoreAsync("session-expired", cancellationToken).ConfigureAwait(false);
            throw new VaultException(VaultErrorCode.SessionExpired, "Vault session expired.");
        }
        if (!_session.RecoveryVerified)
        {
            throw new VaultException(
                VaultErrorCode.RecoveryVerificationRequired,
                "The recovery key must be confirmed before accessing Vault content.");
        }
        return _session;
    }

    private async Task EnsureSessionStillValidAsync(
        VaultSession session,
        CancellationToken cancellationToken)
    {
        RefreshActivity(session);
        if (IsExpired(session))
        {
            await LockCoreAsync("session-expired", cancellationToken).ConfigureAwait(false);
            throw new VaultException(VaultErrorCode.SessionExpired, "Vault session expired.");
        }
    }

    private void SetExpiringState(VaultSession session)
    {
        var now = _timeProvider.GetUtcNow();
        var idleRemaining = session.LastUserActivityAt + VaultSession.IdleTimeout - now;
        var hardRemaining = session.HardExpiresAt - now;
        session.State = idleRemaining <= TimeSpan.FromMinutes(2) ||
            hardRemaining <= TimeSpan.FromMinutes(2)
            ? VaultSessionState.Expiring
            : VaultSessionState.Unlocked;
    }

    private static VaultSessionStatus CreateStatus(VaultSession session, string? lockReason)
    {
        return new VaultSessionStatus(
            session.State,
            session.SessionId,
            session.StartedAt,
            session.LastUserActivityAt,
            session.LastUserActivityAt + VaultSession.IdleTimeout,
            session.HardExpiresAt,
            lockReason,
            session.RecoveryVerified);
    }

    private static VaultSessionStatus GetLockedStatus(string? reason)
    {
        return new VaultSessionStatus(
            VaultSessionState.Locked,
            null,
            null,
            null,
            null,
            null,
            reason,
            false);
    }

    private static string NormalizeVaultPath(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Vault path is required.");
        }

        return Path.GetFullPath(vaultPath);
    }

    private static void RequireUserActor(VaultActorKind actor)
    {
        if (actor is VaultActorKind.Mcp)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "This operation requires an explicit user action.");
        }
    }

    private static async Task VerifyEnvelopeAndDatabaseAsync(
        string vaultPath,
        VaultKeyEnvelope expectedEnvelope,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken,
        string keyFileName = "vault.keys")
    {
        var keyPath = Path.Combine(vaultPath, keyFileName);
        var loaded = await VaultKeyring.LoadEnvelopeAsync(keyPath, cancellationToken)
            .ConfigureAwait(false);
        if (loaded.VaultId != expectedEnvelope.VaultId ||
            loaded.KeyGeneration != expectedEnvelope.KeyGeneration)
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Updated key envelope was not persisted.");
        }

        var material = await VaultKeyring.UnwrapAsync(
            loaded,
            credential,
            credentialType,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await SqlCipherVaultStore.OpenAsync(
                vaultPath,
                material,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.MasterKey);
            CryptographicOperations.ZeroMemory(material.DbKey);
        }
    }

    private static async Task AppendMaintenanceAuditAsync(
        string vaultPath,
        UnlockedKeyMaterial material,
        VaultActorKind actor,
        string action,
        CancellationToken cancellationToken)
    {
        await using var connection = await SqlCipherVaultStore.OpenAsync(
            vaultPath,
            material,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AuditWriter.AppendAsync(
            connection,
            (Microsoft.Data.Sqlite.SqliteTransaction)transaction,
            actor,
            null,
            action,
            null,
            "success",
            null,
            "{}",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkRecoveryVerifiedAndAuditAsync(
        string vaultPath,
        UnlockedKeyMaterial material,
        VaultActorKind actor,
        string action,
        CancellationToken cancellationToken)
    {
        await using var connection = await SqlCipherVaultStore.OpenAsync(
            vaultPath,
            material,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE vault_meta
                SET value = $value
                WHERE key = 'recovery_verified_at_ms';
                """;
            update.Parameters.AddWithValue(
                "$value",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AuditWriter.AppendAsync(
            connection,
            (Microsoft.Data.Sqlite.SqliteTransaction)transaction,
            actor,
            null,
            action,
            null,
            "success",
            null,
            "{}",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RestoreKeyBackup(string keyPath, string? backupPath)
    {
        if (backupPath is not null && File.Exists(backupPath))
        {
            File.Copy(backupPath, keyPath, overwrite: true);
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteIfExists(string? path)
    {
        try
        {
            DeleteIfExists(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
