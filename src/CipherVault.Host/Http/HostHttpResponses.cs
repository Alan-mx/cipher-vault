using CipherVault.Host.Contracts;

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
            HostErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
            HostErrorCode.LockInProgress => StatusCodes.Status409Conflict,
            HostErrorCode.VaultInUse => StatusCodes.Status409Conflict,
            HostErrorCode.AlreadyExists => StatusCodes.Status409Conflict,
            HostErrorCode.NotFound => StatusCodes.Status404NotFound,
            HostErrorCode.MaintenanceInProgress => StatusCodes.Status423Locked,
            HostErrorCode.StorageError => StatusCodes.Status503ServiceUnavailable,
            HostErrorCode.HostUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status503ServiceUnavailable
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
            HostErrorCode.RateLimited => "rate_limited",
            HostErrorCode.LockInProgress => "lock_in_progress",
            HostErrorCode.ConfigMigrationRequired => "config_migration_required",
            HostErrorCode.MaintenanceInProgress => "maintenance_in_progress",
            HostErrorCode.StorageError => "storage_error",
            _ => "host_error"
        };
    }
}
