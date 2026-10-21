namespace CipherVault.Host.Contracts;

public enum HostErrorCode
{
    HostUnavailable,
    VaultInUse,
    AlreadyExists,
    InvalidInput,
    InvalidToken,
    InvalidCredential,
    InvalidOrigin,
    InvalidHost,
    AccessDenied,
    RateLimited,
    LockInProgress,
    ConfigMigrationRequired,
    MaintenanceInProgress,
    StorageError
}
