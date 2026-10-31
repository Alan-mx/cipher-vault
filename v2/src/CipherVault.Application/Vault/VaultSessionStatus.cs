namespace CipherVault.Application.Vault;

public sealed record VaultSessionStatus(
    VaultSessionState State,
    Guid? SessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    DateTimeOffset? IdleExpiresAtUtc,
    VaultLockReason LockReason);
