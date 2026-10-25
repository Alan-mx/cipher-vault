using CipherVault.Core.Errors;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Mcp;

public static class BridgeToolErrorMapper
{
    public static string ToCode(HostException exception)
    {
        return exception.Code switch
        {
            HostErrorCode.HostUnavailable => "host_unavailable",
            HostErrorCode.VaultInUse => "vault_in_use",
            HostErrorCode.InvalidInput => "invalid_input",
            HostErrorCode.AlreadyExists => "already_exists",
            HostErrorCode.NotFound => "not_found",
            HostErrorCode.InvalidToken or
            HostErrorCode.InvalidCredential => "auth_denied",
            HostErrorCode.InvalidOrigin or
            HostErrorCode.InvalidHost => "invalid_input",
            HostErrorCode.Conflict => "conflict",
            HostErrorCode.RateLimited => "rate_limited",
            HostErrorCode.RequestTooLarge => "request_too_large",
            HostErrorCode.LockInProgress => "lock_in_progress",
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
            VaultErrorCode.AccessDenied => "access_denied",
            VaultErrorCode.Conflict => "revision_conflict",
            VaultErrorCode.CorruptVault => "integrity_error",
            VaultErrorCode.SessionLocked => "auth_required",
            VaultErrorCode.SessionExpired => "session_expired",
            VaultErrorCode.RecoveryVerificationRequired =>
                "recovery_verification_required",
            VaultErrorCode.UnsupportedPlatform => "unsupported_platform",
            VaultErrorCode.PatchTargetNotFound => "patch_target_not_found",
            VaultErrorCode.FrontMatterInvalid => "frontmatter_invalid",
            VaultErrorCode.LinkIndexTooLarge => "link_index_too_large",
            VaultErrorCode.InvalidCursor => "invalid_cursor",
            VaultErrorCode.StorageError => "storage_error",
            _ => "storage_error"
        };
    }
}
