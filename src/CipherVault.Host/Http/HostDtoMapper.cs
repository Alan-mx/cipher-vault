using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;

namespace CipherVault.Host.Http;

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

    public static HostAccessGrantData ToAccessGrantData(AccessGrantRecord record)
    {
        return new HostAccessGrantData(
            record.GrantId,
            record.ClientId,
            record.ClientInstanceId,
            AccessGrantScopes.ToNames(record.Scopes)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList(),
            record.State.ToString().ToLowerInvariant(),
            record.CreatedAt,
            record.ApprovedAt,
            record.LastUsedAt,
            record.IdleExpiresAt,
            record.HardExpiresAt);
    }
}
