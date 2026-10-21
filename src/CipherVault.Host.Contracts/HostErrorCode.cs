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
    AccessGrantRequired,
    AccessGrantInvalid,
    AccessGrantExpired,
    AccessGrantRevoked,
    AccessGrantCooldown,
    RateLimited,
    RequestTooLarge,
    LockInProgress,
    SessionLocked,
    Conflict,
    ConfigMigrationRequired,
    MaintenanceInProgress,
    StorageError
}
