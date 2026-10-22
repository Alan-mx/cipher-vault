using System.IO;
using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Session;

public sealed class HostSessionCoordinator
{
    private static readonly TimeSpan DefaultLockDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IVaultService _vault;
    private readonly ResolvedCipherVaultHostConfig _configuration;
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

    public HostSessionCoordinator(
        IVaultService vault,
        ResolvedCipherVaultHostConfig configuration,
        IHostCredentialPrompt credentialPrompt,
        AccessGrantService accessGrants,
        ApprovalStore? approvals = null,
        AdminSessionStore? adminSessions = null,
        TimeSpan? lockDrainTimeout = null)
    {
        _vault = vault;
        _configuration = configuration;
        _credentialPrompt = credentialPrompt;
        _accessGrants = accessGrants;
        _approvals = approvals;
        _adminSessions = adminSessions;
        _lockDrainTimeout = lockDrainTimeout ?? DefaultLockDrainTimeout;
    }

    public async Task<HostSessionSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        bool revokeGrants;
        lock (_stateSync)
        {
            if (_state != VaultSessionState.Locking)
            {
                _state = status.State;
                _lockReason = status.LockReason ?? _lockReason;
                revokeGrants = status.State == VaultSessionState.Locked;
                if (revokeGrants)
                {
                    _leaseCancellation?.Cancel();
                }
            }
            else
            {
                revokeGrants = false;
            }
        }
        if (revokeGrants)
        {
            await _accessGrants.RevokeAllAsync("vault-locked")
                .ConfigureAwait(false);
            _approvals?.RevokeAll();
            _adminSessions?.RevokeAll("vault-locked");
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
            lock (_stateSync)
            {
                if (_state == VaultSessionState.Locking)
                {
                    throw new HostException(
                        HostErrorCode.LockInProgress,
                        "Vault lock is still in progress.");
                }
            }

            var current = await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (current.State == VaultSessionState.Locked)
            {
                await _accessGrants.RevokeAllAsync("vault-relocked")
                    .ConfigureAwait(false);
                _approvals?.RevokeAll();
                _adminSessions?.RevokeAll("vault-relocked");
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
                    _configuration.VaultPath,
                    prompt.Credential,
                    prompt.CredentialType,
                    cancellationToken).ConfigureAwait(false);
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
            await _accessGrants.RevokeAllAsync(reason).ConfigureAwait(false);
            _approvals?.RevokeAll();
            _adminSessions?.RevokeAll(reason);

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
                Path.GetFullPath(_configuration.VaultPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HostException(
                HostErrorCode.InvalidInput,
                "Initialization target must match the configured Vault path.");
        }
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new HostException(
                HostErrorCode.AlreadyExists,
                "Vault initialization requires a path that does not already exist.");
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

            var result = await _vault.InitializeAsync(fullPath, first.Credential, cancellationToken)
                .ConfigureAwait(false);
            return new HostInitializeData(
                result.VaultId,
                result.VaultPath,
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
                Path.GetFullPath(_configuration.VaultPath),
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
                    _configuration.VaultPath,
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
                    _configuration.VaultPath,
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
                    _configuration.VaultPath,
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
            status.RecoveryVerified);
    }
}
