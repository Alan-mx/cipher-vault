namespace CipherVault.Application.Vault;

public enum VaultLockReason
{
    None,
    UserRequested,
    IdleTimeout,
    ProcessExit,
    RuntimeFailure
}
