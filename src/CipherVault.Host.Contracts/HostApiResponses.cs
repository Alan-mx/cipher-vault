namespace CipherVault.Host.Contracts;

public sealed record HostSessionStatusData(
    string State,
    Guid? SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastUserActivityAt,
    DateTimeOffset? IdleExpiresAt,
    DateTimeOffset? HardExpiresAt,
    string? LockReason,
    int ActiveLeases,
    int ConnectedClients,
    bool RecoveryVerified,
    string? LockCleanupWarning = null);

public sealed record HostInitializeData(
    Guid VaultId,
    string VaultPath,
    string RecoveryKey,
    DateTimeOffset CreatedAt);

public sealed record HostRecoveryRotationData(
    string RecoveryKey);
