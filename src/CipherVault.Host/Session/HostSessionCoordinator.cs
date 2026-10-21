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
    private readonly TimeSpan _lockDrainTimeout;
    private VaultSessionState _state = VaultSessionState.Locked;
    private string? _lockReason;
    private int _activeLeases;
    private TaskCompletionSource? _leasesDrained;
    private CancellationTokenSource? _leaseCancellation;
    private bool _maintenanceActive;

    public HostSessionCoordinator(
        IVaultService vault,
        ResolvedCipherVaultHostConfig configuration,
        IHostCredentialPrompt credentialPrompt,
        TimeSpan? lockDrainTimeout = null)
    {
        _vault = vault;
        _configuration = configuration;
        _credentialPrompt = credentialPrompt;
        _lockDrainTimeout = lockDrainTimeout ?? DefaultLockDrainTimeout;
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
            if (current.State is VaultSessionState.Unlocked or VaultSessionState.Expiring)
            {
                EnsureLeaseCancellation();
                return MapAndSetState(current);
            }

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
            if (_state == VaultSessionState.Locking ||
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

    public async Task<T> ExecuteMaintenanceAsync<T>(
        Func<IVaultService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await _vault.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            Task drainedTask;
            lock (_stateSync)
            {
                if (_maintenanceActive)
                {
                    throw new HostException(
                        HostErrorCode.MaintenanceInProgress,
                        "Vault maintenance is already in progress.");
                }
                if (_state == VaultSessionState.Locking ||
                    status.State is not (
                        VaultSessionState.Unlocked or
                        VaultSessionState.Expiring))
                {
                    throw new HostException(
                        HostErrorCode.SessionLocked,
                        "Vault is locked.");
                }

                _maintenanceActive = true;
                _state = status.State;
                if (_activeLeases == 0)
                {
                    drainedTask = Task.CompletedTask;
                }
                else
                {
                    _leasesDrained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drainedTask = _leasesDrained.Task;
                }
            }

            var timeoutTask = Task.Delay(_lockDrainTimeout, cancellationToken);
            if (await Task.WhenAny(drainedTask, timeoutTask).ConfigureAwait(false) !=
                drainedTask)
            {
                throw new HostException(
                    HostErrorCode.MaintenanceInProgress,
                    "Vault operations did not drain before maintenance started.");
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
                    VaultActorKind.UserUi,
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
            _leaseCancellation?.Dispose();
            _leaseCancellation = null;
            _leasesDrained = null;
            return CreateSnapshot(status);
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

    private void EnsureLeaseCancellation()
    {
        lock (_stateSync)
        {
            _leaseCancellation ??= new CancellationTokenSource();
        }
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
            ConnectedClients: 0);
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
            ConnectedClients: 0);
    }
}
