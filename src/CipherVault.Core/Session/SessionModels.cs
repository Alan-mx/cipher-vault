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
