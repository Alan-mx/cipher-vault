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

    internal void Validate()
    {
        if (IdleTimeout < TimeSpan.FromMinutes(1) ||
            IdleTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(IdleTimeout),
                "Idle timeout must be between 1 minute and 24 hours.");
        }
        if (HardLifetime < IdleTimeout ||
            HardLifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardLifetime),
                "Hard lifetime must be at least the idle timeout and no more than 7 days.");
        }
        if (ExpiringWindow < TimeSpan.FromSeconds(10) ||
            ExpiringWindow > IdleTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpiringWindow),
                "Expiring window must be between 10 seconds and the idle timeout.");
        }
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
