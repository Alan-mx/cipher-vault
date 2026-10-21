using CipherVault.Core.Session;

namespace CipherVault.Host.Session;

public sealed class HostSessionCoordinator
{
    private readonly object _sync = new();
    private VaultSessionState _state = VaultSessionState.Locked;
    private string? _lockReason;

    public HostSessionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return CreateSnapshot();
        }
    }

    public Task<HostSessionSnapshot> LockAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _lockReason = reason;
            _state = VaultSessionState.Locked;
            return Task.FromResult(CreateSnapshot());
        }
    }

    private HostSessionSnapshot CreateSnapshot()
    {
        return new HostSessionSnapshot(
            _state,
            SessionId: null,
            StartedAt: null,
            LastUserActivityAt: null,
            IdleExpiresAt: null,
            HardExpiresAt: null,
            LockReason: _lockReason,
            ActiveLeases: 0,
            ConnectedClients: 0);
    }
}
