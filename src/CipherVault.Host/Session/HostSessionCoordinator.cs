using System.IO;
using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Core.Grants;

namespace CipherVault.Host.Session;

public sealed class HostSessionCoordinator : IVaultSessionClosingHandler
{
    private static readonly TimeSpan DefaultLockDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IVaultService _vault;
    private readonly string _vaultPath;
    private readonly IHostCredentialPrompt _credentialPrompt;
    private readonly AccessGrantService _accessGrants;
    private readonly ApprovalStore? _approvals;
    private readonly AdminSessionStore? _adminSessions;
    private readonly TimeSpan _lockDrainTimeout;
    private VaultSessionState _state = VaultSessionState.Locked;
    private string? _lockReason;
    private int _activeLeases;
    private int _connectedClients;
    private TaskCompletionSource? _leasesDrained;
    private CancellationTokenSource? _leaseCancellation;
    private bool _maintenanceActive;
    private bool _grantMutationActive;
    private TaskCompletionSource? _grantMutationDrained;

    public HostSessionCoordinator(
        IVaultService vault,
        string vaultPath,
        IHostCredentialPrompt credentialPrompt,
        AccessGrantService accessGrants,
        ApprovalStore? approvals = null,
        AdminSessionStore? adminSessions = null,
        TimeSpan? lockDrainTimeout = null)
    {
        _vault = vault;
        _vaultPath = vaultPath;
        _credentialPrompt = credentialPrompt;
        _accessGrants = accessGrants;
        _approvals = approvals;
        _adminSessions = adminSessions;
        _lockDrainTimeout = lockDrainTimeout ?? DefaultLockDrainTimeout;
        _vault.SetSessionClosingHandler(this);
    }

    public async Task<HostSessionSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateSync)
        {
            if (_state != VaultSessionState.Locking)
            {
                _state = status.State;
                _lockReason = status.LockReason ?? _lockReason;
            }
        }
        lock (_stateSync)
        {
            return CreateSnapshot(status);
        }
    }

    public async Task<HostSessionSnapshot> UnlockAsync(
        HostCredentialPreference preference,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateSync)
            {
                if (_state == VaultSessionState.Locking &&
                    current.State != VaultSessionState.Locked)
                {
                    throw new HostException(
                        HostErrorCode.LockInProgress,
                        "Vault lock is still in progress.");
                }
                if (_state == VaultSessionState.Locking &&
                    current.State == VaultSessionState.Locked)
                {
                    _state = VaultSessionState.Locked;
                }
            }
            if (current.State is VaultSessionState.Unlocked or VaultSessionState.Expiring)
            {
                EnsureLeaseCancellation();
                return MapAndSetState(current);
            }
            if (current.State == VaultSessionState.Locking)
            {
                throw new HostException(
                    HostErrorCode.LockInProgress,
                    "Vault lock is still in progress.");
            }

            await DrainLeasesForUnlockAsync(cancellationToken).ConfigureAwait(false);

            var prompt = await _credentialPrompt.RequestAsync(
                preference,
                message: null,
                cancellationToken).ConfigureAwait(false);
            if (!prompt.Confirmed || string.IsNullOrEmpty(prompt.Credential))
            {
                throw new HostException(
                    HostErrorCode.AccessDenied,
                    "Vault authorization was cancelled.");
            }

            try
            {
                var unlocked = await _vault.UnlockAsync(
                    _vaultPath,
                    prompt.Credential,
                    prompt.CredentialType,
                    cancellationToken).ConfigureAwait(false);
                await _accessGrants.InitializeStoredSessionAsync(cancellationToken)
                    .ConfigureAwait(false);
                EnsureLeaseCancellation();
                return MapAndSetState(unlocked);
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.InvalidCredential)
            {
                throw new HostException(
                    HostErrorCode.InvalidCredential,
                    "Vault credential is invalid.",
                    exception);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<HostSessionSnapshot> LockAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task drainedTask;
            lock (_stateSync)
            {
                if (_state == VaultSessionState.Locked)
                {
                    _lockReason = reason;
                    return CreateSnapshotFromLocalState();
                }

                _state = VaultSessionState.Locking;
                _lockReason = reason;
                _leaseCancellation?.Cancel();
                if (_activeLeases == 0)
                {
                    drainedTask = Task.CompletedTask;
                }
                else
                {
                    _leasesDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drainedTask = _leasesDrained.Task;
                }
            }

            var timeoutTask = Task.Delay(_lockDrainTimeout, cancellationToken);
            if (await Task.WhenAny(drainedTask, timeoutTask).ConfigureAwait(false) != drainedTask)
            {
                _ = CompleteLockAfterDrainAsync(drainedTask, reason);
                return CreateSnapshotFromLocalState();
            }

            return await FinalizeLockAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<HostVaultLease> AcquireLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_stateSync)
        {
            if (_maintenanceActive)
            {
                throw new HostException(
                    HostErrorCode.MaintenanceInProgress,
                    "Vault maintenance is in progress.");
            }
            if (_grantMutationActive)
            {
                throw new HostException(
                    HostErrorCode.Conflict,
                    "Access Grant state is changing.");
            }
        }

        var status = await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateSync)
        {
            if (_maintenanceActive)
            {
                throw new HostException(
                    HostErrorCode.MaintenanceInProgress,
                    "Vault maintenance is in progress.");
            }
            if (_grantMutationActive)
            {
                throw new HostException(
                    HostErrorCode.Conflict,
                    "Access Grant state is changing.");
            }
            if (_state is not (
                    VaultSessionState.Unlocked or
                    VaultSessionState.Expiring) ||
                status.SessionId is null ||
                status.State is not (VaultSessionState.Unlocked or VaultSessionState.Expiring))
            {
                throw new HostException(
                    HostErrorCode.SessionLocked,
                    "Vault is locked.");
            }

            _state = status.State;
            _activeLeases++;
            var leaseToken = _leaseCancellation?.Token ?? CancellationToken.None;
            return new HostVaultLease(
                Guid.NewGuid(),
                status.SessionId,
                leaseToken,
                ReleaseLease);
        }
    }

    public HostClientConnection EnterClient()
    {
        lock (_stateSync)
        {
            _connectedClients++;
        }
        return new HostClientConnection(ReleaseClient);
    }

    public async Task<T> ExecuteGrantMutationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task drainedTask;
            lock (_stateSync)
            {
                if (_grantMutationActive)
                {
                    throw new HostException(
                        HostErrorCode.Conflict,
                        "Access Grant mutation is already in progress.");
                }
                _grantMutationActive = true;
                if (_activeLeases == 0)
                {
                    drainedTask = Task.CompletedTask;
                }
                else
                {
                    _grantMutationDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drainedTask = _grantMutationDrained.Task;
                }
            }

            try
            {
                await drainedTask.WaitAsync(
                    _lockDrainTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new HostException(
                    HostErrorCode.Conflict,
                    "Vault operations did not drain before Access Grant mutation.");
            }

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateSync)
            {
                _grantMutationActive = false;
                _grantMutationDrained = null;
            }
            _operationGate.Release();
        }
    }

    async Task IVaultSessionClosingHandler.HandleClosingAsync(
        VaultSessionClosingContext context,
        IAccessGrantStore store,
        CancellationToken cancellationToken)
    {
        _ = store;
        lock (_stateSync)
        {
            _state = VaultSessionState.Locking;
            _lockReason = context.Reason;
            _leaseCancellation?.Cancel();
        }
        try
        {
            await _accessGrants.RevokeAllAsync("vault-locked")
                .ConfigureAwait(false);
            _approvals?.RevokeAll();
            _adminSessions?.RevokeAll(context.Reason);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_stateSync)
            {
                _state = VaultSessionState.Locked;
                _lockReason = context.Reason;
            }
        }
    }

    public Task<T> ExecuteMaintenanceAsync<T>(
        Func<IVaultService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        return ExecuteMaintenanceCoreAsync(
            operation,
            allowLocked: false,
            cancellationToken);
    }

    private async Task<T> ExecuteMaintenanceCoreAsync<T>(
        Func<IVaultService, CancellationToken, Task<T>> operation,
        bool allowLocked,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await _vault.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            var drainedTask = Task.CompletedTask;
            var waitForLeases = false;
            lock (_stateSync)
            {
                if (_maintenanceActive)
                {
                    throw new HostException(
                        HostErrorCode.MaintenanceInProgress,
                        "Vault maintenance is already in progress.");
                }
                var isLocked = status.State == VaultSessionState.Locked;
                if (_state == VaultSessionState.Locking ||
                    (!isLocked &&
                        status.State is not (
                            VaultSessionState.Unlocked or
                            VaultSessionState.Expiring)) ||
                    (isLocked && !allowLocked))
                {
                    throw new HostException(
                        HostErrorCode.SessionLocked,
                        "Vault is locked.");
                }

                _maintenanceActive = true;
                _state = status.State;
                if (isLocked)
                {
                    _leaseCancellation?.Cancel();
                }
                else if (_activeLeases == 0)
                {
                    drainedTask = Task.CompletedTask;
                }
                else
                {
                    _leasesDrained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drainedTask = _leasesDrained.Task;
                    waitForLeases = true;
                }
            }

            if (waitForLeases)
            {
                var timeoutTask = Task.Delay(_lockDrainTimeout, cancellationToken);
                if (await Task.WhenAny(drainedTask, timeoutTask).ConfigureAwait(false) !=
                    drainedTask)
                {
                    throw new HostException(
                        HostErrorCode.MaintenanceInProgress,
                        "Vault operations did not drain before maintenance started.");
                }
            }

            return await operation(_vault, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateSync)
            {
                _maintenanceActive = false;
                if (_activeLeases == 0)
                {
                    _leasesDrained = null;
                }
            }
            _operationGate.Release();
        }
    }

    public async Task<HostInitializeData> InitializeAsync(
        string vaultPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(vaultPath);
        if (!string.Equals(
                fullPath,
                Path.GetFullPath(_vaultPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HostException(
                HostErrorCode.InvalidInput,
                "Initialization target must match the configured Vault path.");
        }
        if (File.Exists(fullPath))
        {
            throw new HostException(
                HostErrorCode.AlreadyExists,
                "Vault initialization requires a path that does not already exist.");
        }
        var fixedLayoutInitialization = Directory.Exists(fullPath);
        if (fixedLayoutInitialization &&
            !IsEmptyStrictVaultLayout(fullPath))
        {
            throw new HostException(
                HostErrorCode.AlreadyExists,
                "Vault initialization requires an empty strict Vault layout.");
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var first = await _credentialPrompt.RequestAsync(
                HostCredentialPreference.Password,
                "设置新的资料库密码。",
                cancellationToken).ConfigureAwait(false);
            if (!first.Confirmed || string.IsNullOrEmpty(first.Credential))
            {
                throw new HostException(
                    HostErrorCode.AccessDenied,
                    "Vault initialization was cancelled.");
            }

            var second = await _credentialPrompt.RequestAsync(
                HostCredentialPreference.Password,
                "再次输入资料库密码以确认。",
                cancellationToken).ConfigureAwait(false);
            if (!second.Confirmed ||
                string.IsNullOrEmpty(second.Credential) ||
                !string.Equals(
                    first.Credential,
                    second.Credential,
                    StringComparison.Ordinal))
            {
                throw new HostException(
                    HostErrorCode.InvalidInput,
                    "Passwords do not match.");
            }

            var initializationPath = fixedLayoutInitialization
                ? Path.Combine(
                    Path.GetDirectoryName(fullPath)
                        ?? throw new HostException(
                            HostErrorCode.InvalidInput,
                            "Vault path has no parent."),
                    $".{Path.GetFileName(fullPath)}.init-{Guid.NewGuid():N}")
                : fullPath;
            var result = await _vault.InitializeAsync(
                    initializationPath,
                    first.Credential,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fixedLayoutInitialization)
            {
                var movedFiles = new List<string>();
                try
                {
                    foreach (var fileName in new[] { "vault.db", "vault.keys" })
                    {
                        var source = Path.Combine(initializationPath, fileName);
                        var destination = Path.Combine(fullPath, fileName);
                        File.Move(source, destination);
                        movedFiles.Add(destination);
                    }
                    Directory.Delete(initializationPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    foreach (var file in movedFiles)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception cleanupException) when (
                            cleanupException is IOException or
                            UnauthorizedAccessException)
                        {
                        }
                    }
                    TryDeleteDirectory(initializationPath);
                    throw new HostException(
                        HostErrorCode.StorageError,
                        "Initialized Vault could not be moved into the fixed layout.",
                        exception);
                }
            }
            return new HostInitializeData(
                result.VaultId,
                fullPath,
                result.RecoveryKey,
                result.CreatedAt);
        }
        catch (VaultException exception) when (
            exception.Code is VaultErrorCode.InvalidInput or
                VaultErrorCode.AlreadyExists)
        {
            throw new HostException(
                exception.Code == VaultErrorCode.AlreadyExists
                    ? HostErrorCode.AlreadyExists
                    : HostErrorCode.InvalidInput,
                exception.Message,
                exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<VaultRestoreBackupResult> RestoreBackupToNewAsync(
        string backupPath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        return await RestoreBackupToNewAsync(
            backupPath,
            targetPath,
            VaultActorKind.UserUi,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultRestoreBackupResult> RestoreBackupToNewAsync(
        string backupPath,
        string targetPath,
        VaultActorKind actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath) ||
            string.IsNullOrWhiteSpace(targetPath))
        {
            throw new HostException(
                HostErrorCode.InvalidInput,
                "backupPath and targetPath are required.");
        }

        var fullTargetPath = Path.GetFullPath(targetPath);
        if (string.Equals(
                fullTargetPath,
                Path.GetFullPath(_vaultPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HostException(
                HostErrorCode.InvalidInput,
                "Online restore cannot overwrite the active Vault.");
        }
        if (Directory.Exists(fullTargetPath) || File.Exists(fullTargetPath))
        {
            throw new HostException(
                HostErrorCode.AlreadyExists,
                "Restore target must be a new directory.");
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prompt = await _credentialPrompt.RequestAsync(
                HostCredentialPreference.Any,
                "输入备份密码或恢复密钥以恢复到新目录。",
                cancellationToken).ConfigureAwait(false);
            if (!prompt.Confirmed || string.IsNullOrEmpty(prompt.Credential))
            {
                throw new HostException(
                    HostErrorCode.AccessDenied,
                    "Backup restore authorization was cancelled.");
            }

            try
            {
                return await _vault.RestoreBackupAsync(
                    Path.GetFullPath(backupPath),
                    fullTargetPath,
                    prompt.Credential,
                    prompt.CredentialType,
                    actor,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.InvalidCredential)
            {
                throw new HostException(
                    HostErrorCode.InvalidCredential,
                    "Backup credential is invalid.",
                    exception);
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.InvalidInput)
            {
                throw new HostException(
                    HostErrorCode.InvalidInput,
                    exception.Message,
                    exception);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ChangePasswordAsync(
        CancellationToken cancellationToken = default)
    {
        await ChangePasswordAsync(
            VaultActorKind.UserUi,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ChangePasswordAsync(
        VaultActorKind actor,
        CancellationToken cancellationToken = default)
    {
        await ExecuteMaintenanceAsync(
            async (vault, token) =>
            {
                var current = await _credentialPrompt.RequestAsync(
                    HostCredentialPreference.Password,
                    "输入当前资料库密码。",
                    token).ConfigureAwait(false);
                if (!current.Confirmed || string.IsNullOrEmpty(current.Credential))
                {
                    throw new HostException(
                        HostErrorCode.AccessDenied,
                        "Password change authorization was cancelled.");
                }

                var next = await _credentialPrompt.RequestAsync(
                    HostCredentialPreference.Password,
                    "输入新的资料库密码。",
                    token).ConfigureAwait(false);
                var confirmation = await _credentialPrompt.RequestAsync(
                    HostCredentialPreference.Password,
                    "再次输入新的资料库密码。",
                    token).ConfigureAwait(false);
                if (!next.Confirmed ||
                    string.IsNullOrEmpty(next.Credential) ||
                    !confirmation.Confirmed ||
                    string.IsNullOrEmpty(confirmation.Credential) ||
                    !string.Equals(
                        next.Credential,
                        confirmation.Credential,
                        StringComparison.Ordinal))
                {
                    throw new HostException(
                        HostErrorCode.InvalidInput,
                        "New passwords do not match.");
                }

                await vault.ChangePasswordAsync(
                    _vaultPath,
                    current.Credential,
                    next.Credential,
                    actor,
                    token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RotateRecoveryKeyAsync(
        CancellationToken cancellationToken = default)
    {
        return await RotateRecoveryKeyAsync(
            VaultActorKind.UserUi,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RotateRecoveryKeyAsync(
        VaultActorKind actor,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteMaintenanceAsync(
            async (vault, token) =>
            {
                var prompt = await _credentialPrompt.RequestAsync(
                    HostCredentialPreference.Password,
                    "输入当前资料库密码以轮换恢复密钥。",
                    token).ConfigureAwait(false);
                if (!prompt.Confirmed || string.IsNullOrEmpty(prompt.Credential))
                {
                    throw new HostException(
                        HostErrorCode.AccessDenied,
                        "Recovery key rotation authorization was cancelled.");
                }

                return await vault.RotateRecoveryKeyAsync(
                    _vaultPath,
                    prompt.Credential,
                    actor,
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfirmRecoveryKeyAsync(
        CancellationToken cancellationToken = default)
    {
        await ConfirmRecoveryKeyAsync(
            VaultActorKind.UserUi,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfirmRecoveryKeyAsync(
        VaultActorKind actor,
        CancellationToken cancellationToken = default)
    {
        await ExecuteMaintenanceCoreAsync(
            async (vault, token) =>
            {
                var prompt = await _credentialPrompt.RequestAsync(
                    HostCredentialPreference.RecoveryKey,
                    "输入新的恢复密钥以完成确认。",
                    token).ConfigureAwait(false);
                if (!prompt.Confirmed || string.IsNullOrEmpty(prompt.Credential))
                {
                    throw new HostException(
                        HostErrorCode.AccessDenied,
                        "Recovery key confirmation was cancelled.");
                }
                if (prompt.CredentialType != VaultCredentialType.RecoveryKey)
                {
                    throw new HostException(
                        HostErrorCode.InvalidInput,
                        "The confirmation credential must be a recovery key.");
                }

                await vault.ConfirmRecoveryKeyAsync(
                    _vaultPath,
                    prompt.Credential,
                    actor,
                    token).ConfigureAwait(false);
                return true;
            },
            allowLocked: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostSessionSnapshot> FinalizeLockAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var status = await _vault.LockAsync(reason, cancellationToken)
            .ConfigureAwait(false);
        lock (_stateSync)
        {
            _state = VaultSessionState.Locked;
            _lockReason = reason;
            ClearLeaseCancellation();
            return CreateSnapshot(status);
        }
    }

    private async Task DrainLeasesForUnlockAsync(
        CancellationToken cancellationToken)
    {
        Task drainedTask;
        lock (_stateSync)
        {
            if (_activeLeases == 0)
            {
                _state = VaultSessionState.Locked;
                ClearLeaseCancellation();
                return;
            }

            _state = VaultSessionState.Locking;
            _leaseCancellation?.Cancel();
            _leasesDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            drainedTask = _leasesDrained.Task;
        }

        try
        {
            await drainedTask.WaitAsync(
                _lockDrainTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_stateSync)
            {
                _state = VaultSessionState.Locked;
            }
            throw new HostException(
                HostErrorCode.LockInProgress,
                "Vault operations did not drain before unlock.");
        }
        catch
        {
            lock (_stateSync)
            {
                _state = VaultSessionState.Locked;
            }
            throw;
        }

        lock (_stateSync)
        {
            _state = VaultSessionState.Locked;
            ClearLeaseCancellation();
        }
    }

    private async Task CompleteLockAfterDrainAsync(Task drainedTask, string reason)
    {
        try
        {
            await drainedTask.ConfigureAwait(false);
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_stateSync)
                {
                    if (_state != VaultSessionState.Locking)
                    {
                        return;
                    }
                }
                await FinalizeLockAsync(reason, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch
        {
            // Host shutdown or a failed background lock remains visible through status/logging.
        }
    }

    private void ReleaseLease()
    {
        lock (_stateSync)
        {
            if (_activeLeases > 0)
            {
                _activeLeases--;
            }
            if (_activeLeases == 0)
            {
                _leasesDrained?.TrySetResult();
                _grantMutationDrained?.TrySetResult();
            }
        }
    }

    private void ReleaseClient()
    {
        lock (_stateSync)
        {
            if (_connectedClients > 0)
            {
                _connectedClients--;
            }
        }
    }

    private void EnsureLeaseCancellation()
    {
        lock (_stateSync)
        {
            _leaseCancellation ??= new CancellationTokenSource();
        }
    }

    private void ClearLeaseCancellation()
    {
        _leaseCancellation?.Dispose();
        _leaseCancellation = null;
        _leasesDrained = null;
    }

    private HostSessionSnapshot MapAndSetState(VaultSessionStatus status)
    {
        lock (_stateSync)
        {
            _state = status.State;
            _lockReason = status.LockReason;
            return CreateSnapshot(status);
        }
    }

    private HostSessionSnapshot CreateSnapshotFromLocalState()
    {
        return new HostSessionSnapshot(
            _state,
            SessionId: null,
            StartedAt: null,
            LastUserActivityAt: null,
            IdleExpiresAt: null,
            HardExpiresAt: null,
            LockReason: _lockReason,
            ActiveLeases: _activeLeases,
            ConnectedClients: _connectedClients,
            RecoveryVerified: false);
    }

    private HostSessionSnapshot CreateSnapshot(VaultSessionStatus status)
    {
        return new HostSessionSnapshot(
            _state == VaultSessionState.Locking ? VaultSessionState.Locking : status.State,
            status.SessionId,
            status.StartedAt,
            status.LastUserActivityAt,
            status.IdleExpiresAt,
            status.HardExpiresAt,
            _lockReason ?? status.LockReason,
            _activeLeases,
            ConnectedClients: _connectedClients,
            status.RecoveryVerified,
            status.LockCleanupWarning);
    }

    private static bool IsEmptyStrictVaultLayout(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Select(Path.GetFileName)
                .ToArray();
            return entries.Length == 1 &&
                string.Equals(
                    entries[0],
                    "vault.mode",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new HostException(
                HostErrorCode.StorageError,
                "Vault initialization layout could not be inspected.",
                exception);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
