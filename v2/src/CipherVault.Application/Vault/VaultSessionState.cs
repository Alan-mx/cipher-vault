namespace CipherVault.Application.Vault;

public enum VaultSessionState
{
    Uninitialized,
    Initializing,
    Locked,
    Unlocking,
    Unlocked,
    Locking,
    LockFailed
}
