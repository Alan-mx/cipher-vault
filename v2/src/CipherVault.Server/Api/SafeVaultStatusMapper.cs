using CipherVault.Application.Vault;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Api;

public static class SafeVaultStatusMapper
{
    public static SafeVaultStatus Map(VaultSessionStatus status) =>
        new(
            Wire(status.State),
            IsInitialized(status.State),
            status.State == VaultSessionState.Unlocked,
            status.StartedAtUtc,
            status.LastActivityAtUtc,
            status.IdleExpiresAtUtc,
            Wire(status.LockReason));

    private static bool IsInitialized(VaultSessionState state) =>
        state is not VaultSessionState.Uninitialized;

    private static string Wire(VaultSessionState state) =>
        state switch
        {
            VaultSessionState.Uninitialized => "uninitialized",
            VaultSessionState.Initializing => "initializing",
            VaultSessionState.Locked => "locked",
            VaultSessionState.Unlocking => "unlocking",
            VaultSessionState.Unlocked => "unlocked",
            VaultSessionState.Locking => "locking",
            VaultSessionState.LockFailed => "lock_failed",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

    private static string Wire(VaultLockReason reason) =>
        reason switch
        {
            VaultLockReason.None => "none",
            VaultLockReason.UserRequested => "user_requested",
            VaultLockReason.IdleTimeout => "idle_timeout",
            VaultLockReason.ProcessExit => "process_exit",
            VaultLockReason.RuntimeFailure => "runtime_failure",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
}
