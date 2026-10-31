namespace CipherVault.Application.Vault;

internal static class VaultIdlePolicy
{
    internal static TimeSpan DefaultIdleTimeout => TimeSpan.FromMinutes(10);

    internal static TimeSpan MinimumIdleTimeout => TimeSpan.FromMinutes(1);

    internal static TimeSpan MaximumIdleTimeout => TimeSpan.FromMinutes(120);

    internal static TimeSpan DefaultCheckInterval => TimeSpan.FromSeconds(1);
}
