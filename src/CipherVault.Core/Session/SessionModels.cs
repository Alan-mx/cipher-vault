namespace CipherVault.Core.Session;

public enum VaultCredentialType
{
    Password,
    RecoveryKey
}

public enum VaultSessionState
{
    Locked,
    Unlocking,
    Unlocked,
    Expiring,
    Locking
}

public sealed record VaultInitializeResult(
    Guid VaultId,
    string VaultPath,
    string RecoveryKey,
    DateTimeOffset CreatedAt);

public sealed record VaultSessionOptions(
    TimeSpan IdleTimeout,
    TimeSpan HardLifetime,
    TimeSpan ExpiringWindow)
{
    public static VaultSessionOptions Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(4),
        TimeSpan.FromMinutes(2));

    public static VaultSessionOptions FromMinutes(int idleMinutes, int hardHours)
    {
        var idleTimeout = TimeSpan.FromMinutes(idleMinutes);
        var warningSeconds = Math.Clamp(idleTimeout.TotalSeconds / 4, 10, 120);
        return new VaultSessionOptions(
            idleTimeout,
            TimeSpan.FromHours(hardHours),
            TimeSpan.FromSeconds(warningSeconds));
    }
}

public sealed record VaultSessionStatus(
    VaultSessionState State,
    Guid? SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastUserActivityAt,
    DateTimeOffset? IdleExpiresAt,
    DateTimeOffset? HardExpiresAt,
    string? LockReason,
    bool RecoveryVerified = false);

public sealed record SystemActivitySnapshot(
    TimeSpan SystemIdleTime,
    bool IsWorkstationLocked);

public interface ISystemActivityProvider
{
    SystemActivitySnapshot GetSnapshot();
}
