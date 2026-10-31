using CipherVault.Core.Errors;

namespace CipherVault.Application.Vault;

public sealed record VaultSessionOptions
{
    public static VaultSessionOptions Default { get; } = new(TimeSpan.FromMinutes(10));

    public VaultSessionOptions(TimeSpan idleTimeout)
    {
        if (idleTimeout < TimeSpan.FromMinutes(1)
            || idleTimeout > TimeSpan.FromMinutes(120))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Idle timeout must be between 1 and 120 minutes.");
        }

        IdleTimeout = idleTimeout;
    }

    public TimeSpan IdleTimeout { get; }
}

internal sealed record VaultSessionTimingOptions
{
    public static VaultSessionTimingOptions Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5));

    public VaultSessionTimingOptions(TimeSpan cancelGrace, TimeSpan interruptGrace)
    {
        if (cancelGrace <= TimeSpan.Zero
            || cancelGrace >= TimeSpan.FromMinutes(1)
            || interruptGrace <= TimeSpan.Zero
            || interruptGrace >= TimeSpan.FromMinutes(1))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Session lock grace values are invalid.");
        }

        CancelGrace = cancelGrace;
        InterruptGrace = interruptGrace;
    }

    public TimeSpan CancelGrace { get; }

    public TimeSpan InterruptGrace { get; }

    public TimeSpan TotalLockDeadline => CancelGrace + InterruptGrace;
}
