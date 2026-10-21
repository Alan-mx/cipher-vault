namespace CipherVault.Host.Contracts;

public enum HostErrorCode
{
    HostUnavailable,
    VaultInUse,
    InvalidToken,
    InvalidOrigin,
    InvalidHost,
    AccessDenied,
    RateLimited,
    LockInProgress,
    ConfigMigrationRequired,
    MaintenanceInProgress
}
