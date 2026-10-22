using CipherVault.Host.Contracts;
using CipherVault.Core.Errors;

namespace CipherVault.Host.Http;

internal static class HostHttpResponses
{
    public static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        HostErrorCode code,
        string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        var requestId = context.Items.TryGetValue(
                HostHttpContextKeys.RequestId,
                out var requestIdValue)
            ? requestIdValue as string
            : null;
        return context.Response.WriteAsJsonAsync(
            new HostApiErrorResponse(
                false,
                new HostApiError(ToCode(code), message, requestId)));
    }

    public static int ToStatusCode(HostErrorCode code)
    {
        return code switch
        {
            HostErrorCode.InvalidInput => StatusCodes.Status400BadRequest,
            HostErrorCode.InvalidToken => StatusCodes.Status401Unauthorized,
            HostErrorCode.InvalidCredential => StatusCodes.Status403Forbidden,
            HostErrorCode.InvalidOrigin => StatusCodes.Status403Forbidden,
            HostErrorCode.InvalidHost => StatusCodes.Status403Forbidden,
            HostErrorCode.AccessDenied => StatusCodes.Status403Forbidden,
            HostErrorCode.AccessGrantRequired => StatusCodes.Status403Forbidden,
            HostErrorCode.AccessGrantInvalid => StatusCodes.Status403Forbidden,
            HostErrorCode.AccessGrantExpired => StatusCodes.Status403Forbidden,
            HostErrorCode.AccessGrantRevoked => StatusCodes.Status403Forbidden,
            HostErrorCode.AccessGrantCooldown =>
                StatusCodes.Status429TooManyRequests,
            HostErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
            HostErrorCode.RequestTooLarge =>
                StatusCodes.Status413PayloadTooLarge,
            HostErrorCode.LockInProgress => StatusCodes.Status409Conflict,
            HostErrorCode.SessionLocked => StatusCodes.Status423Locked,
            HostErrorCode.Conflict => StatusCodes.Status409Conflict,
            HostErrorCode.VaultInUse => StatusCodes.Status409Conflict,
            HostErrorCode.AlreadyExists => StatusCodes.Status409Conflict,
            HostErrorCode.NotFound => StatusCodes.Status404NotFound,
            HostErrorCode.MaintenanceInProgress => StatusCodes.Status423Locked,
            HostErrorCode.StorageError => StatusCodes.Status503ServiceUnavailable,
            HostErrorCode.HostUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status503ServiceUnavailable
        };
    }

    public static int ToStatusCode(VaultException exception)
    {
        return exception.Code switch
        {
            VaultErrorCode.InvalidInput => StatusCodes.Status400BadRequest,
            VaultErrorCode.NotFound => StatusCodes.Status404NotFound,
            VaultErrorCode.InvalidCredential or VaultErrorCode.AccessDenied =>
                StatusCodes.Status403Forbidden,
            VaultErrorCode.Conflict or VaultErrorCode.AlreadyExists =>
                StatusCodes.Status409Conflict,
            VaultErrorCode.SessionLocked or VaultErrorCode.SessionExpired =>
                StatusCodes.Status423Locked,
            _ => StatusCodes.Status503ServiceUnavailable
        };
    }

    public static HostErrorCode ToHostErrorCode(VaultException exception)
    {
        return exception.Code switch
        {
            VaultErrorCode.InvalidInput => HostErrorCode.InvalidInput,
            VaultErrorCode.NotFound => HostErrorCode.NotFound,
            VaultErrorCode.InvalidCredential => HostErrorCode.InvalidCredential,
            VaultErrorCode.AccessDenied => HostErrorCode.AccessDenied,
            VaultErrorCode.Conflict => HostErrorCode.Conflict,
            VaultErrorCode.AlreadyExists => HostErrorCode.AlreadyExists,
            VaultErrorCode.SessionLocked or VaultErrorCode.SessionExpired =>
                HostErrorCode.SessionLocked,
            _ => HostErrorCode.StorageError
        };
    }

    private static string ToCode(HostErrorCode code)
    {
        return code switch
        {
            HostErrorCode.HostUnavailable => "host_unavailable",
            HostErrorCode.VaultInUse => "vault_in_use",
            HostErrorCode.AlreadyExists => "already_exists",
            HostErrorCode.NotFound => "not_found",
            HostErrorCode.InvalidInput => "invalid_input",
            HostErrorCode.InvalidToken => "invalid_token",
            HostErrorCode.InvalidCredential => "invalid_credential",
            HostErrorCode.InvalidOrigin => "invalid_origin",
            HostErrorCode.InvalidHost => "invalid_host",
            HostErrorCode.AccessDenied => "access_denied",
            HostErrorCode.AccessGrantRequired => "access_grant_required",
            HostErrorCode.AccessGrantInvalid => "access_grant_invalid",
            HostErrorCode.AccessGrantExpired => "access_grant_expired",
            HostErrorCode.AccessGrantRevoked => "access_grant_revoked",
            HostErrorCode.AccessGrantCooldown => "access_grant_cooldown",
            HostErrorCode.RateLimited => "rate_limited",
            HostErrorCode.RequestTooLarge => "request_too_large",
            HostErrorCode.LockInProgress => "lock_in_progress",
            HostErrorCode.SessionLocked => "session_locked",
            HostErrorCode.Conflict => "conflict",
            HostErrorCode.ConfigMigrationRequired => "config_migration_required",
            HostErrorCode.MaintenanceInProgress => "maintenance_in_progress",
            HostErrorCode.StorageError => "storage_error",
            _ => "host_error"
        };
    }
}
