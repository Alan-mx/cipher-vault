using CipherVault.Core;
using CipherVault.Core.Configuration;
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
        var status = await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateSync)
        {
            if (_state == VaultSessionState.Locking ||
                status.State is not (VaultSessionState.Unlocked or VaultSessionState.Expiring))
            {
                throw new HostException(
                    HostErrorCode.AccessDenied,
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
