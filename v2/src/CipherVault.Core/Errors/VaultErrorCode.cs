namespace CipherVault.Core.Errors;

public enum VaultErrorCode
{
    InvalidInput,
    InvalidCredential,
    CorruptVault,
    UnsupportedVersion,
    UnsupportedPlatform,
    StorageError,
    DatabaseError,
    VaultNotInitialized,
    VaultAlreadyInitialized,
    VaultLocked,
    VaultBusy,
    VaultLockFailed
}
