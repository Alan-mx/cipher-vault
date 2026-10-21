using CipherVault.Core.Session;

namespace CipherVault.Host.Session;

public sealed record HostSessionSnapshot(
    VaultSessionState State,
    Guid? SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastUserActivityAt,
    DateTimeOffset? IdleExpiresAt,
    DateTimeOffset? HardExpiresAt,
    string? LockReason,
    int ActiveLeases,
    int ConnectedClients);
