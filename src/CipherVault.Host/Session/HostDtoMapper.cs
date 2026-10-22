using CipherVault.Host.Contracts;

namespace CipherVault.Host.Session;

internal static class HostDtoMapper
{
    public static HostSessionStatusData ToSessionStatus(HostSessionSnapshot snapshot)
    {
        return new HostSessionStatusData(
            snapshot.State.ToString().ToLowerInvariant(),
            snapshot.SessionId,
            snapshot.StartedAt,
            snapshot.LastUserActivityAt,
            snapshot.IdleExpiresAt,
            snapshot.HardExpiresAt,
            snapshot.LockReason,
            snapshot.ActiveLeases,
            snapshot.ConnectedClients,
            snapshot.RecoveryVerified);
    }
}
