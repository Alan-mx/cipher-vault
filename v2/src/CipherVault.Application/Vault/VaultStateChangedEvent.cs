namespace CipherVault.Application.Vault;

public sealed record VaultStateChangedEvent(
    VaultSessionStatus Status,
    DateTimeOffset ObservedAtUtc);
