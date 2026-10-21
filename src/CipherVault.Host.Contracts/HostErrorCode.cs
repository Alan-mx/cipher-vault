namespace CipherVault.Host.Contracts;

public enum HostErrorCode
{
    HostUnavailable,
    VaultInUse,
    InvalidInput,
    InvalidToken,
    InvalidCredential,
    InvalidOrigin,
    InvalidHost,
    AccessDenied,
    RateLimited,
    LockInProgress,
    ConfigMigrationRequired,
    MaintenanceInProgress
}
