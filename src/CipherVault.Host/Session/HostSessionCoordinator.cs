using CipherVault.Core.Session;

namespace CipherVault.Host.Session;

public sealed class HostSessionCoordinator
{
    private readonly object _sync = new();

    public HostSessionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new HostSessionSnapshot(
                VaultSessionState.Locked,
                SessionId: null,
                StartedAt: null,
                LastUserActivityAt: null,
                IdleExpiresAt: null,
                HardExpiresAt: null,
                LockReason: null,
                ActiveLeases: 0,
                ConnectedClients: 0);
        }
    }
}
