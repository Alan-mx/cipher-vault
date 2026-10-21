namespace CipherVault.Host.Contracts;

public enum HostErrorCode
{
    HostUnavailable,
    VaultInUse,
    AlreadyExists,
    NotFound,
    InvalidInput,
    InvalidToken,
    InvalidCredential,
    InvalidOrigin,
    InvalidHost,
    AccessDenied,
    RateLimited,
    LockInProgress,
    SessionLocked,
    Conflict,
    ConfigMigrationRequired,
    MaintenanceInProgress,
    StorageError
}
