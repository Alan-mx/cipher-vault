using CipherVault.Core.Errors;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Mcp;

internal static class BridgeToolErrorMapper
{
    public static string ToCode(HostException exception)
    {
        return exception.Code switch
        {
            HostErrorCode.InvalidInput => "invalid_input",
            HostErrorCode.AlreadyExists => "already_exists",
            HostErrorCode.NotFound => "not_found",
            HostErrorCode.Conflict => "conflict",
            HostErrorCode.SessionLocked => "auth_required",
            HostErrorCode.MaintenanceInProgress => "maintenance_in_progress",
            HostErrorCode.AccessDenied => "access_denied",
            HostErrorCode.AccessGrantRequired => "access_grant_required",
            HostErrorCode.AccessGrantInvalid => "access_grant_invalid",
            HostErrorCode.AccessGrantExpired => "access_grant_expired",
            HostErrorCode.AccessGrantRevoked => "access_grant_revoked",
            HostErrorCode.AccessGrantCooldown => "access_grant_cooldown",
            _ => "storage_error"
        };
    }

    public static string ToCode(VaultException exception)
    {
        return exception.Code switch
        {
            VaultErrorCode.InvalidInput => "invalid_input",
            VaultErrorCode.AlreadyExists => "already_exists",
            VaultErrorCode.NotFound => "not_found",
            VaultErrorCode.InvalidCredential => "auth_denied",
            VaultErrorCode.Conflict => "conflict",
            VaultErrorCode.CorruptVault => "integrity_error",
            VaultErrorCode.SessionLocked => "auth_required",
            VaultErrorCode.SessionExpired => "session_expired",
            VaultErrorCode.RecoveryVerificationRequired =>
                "recovery_verification_required",
            _ => "storage_error"
        };
    }
}
