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

    private static string ToCode(HostErrorCode code)
    {
        return code switch
        {
            HostErrorCode.HostUnavailable => "host_unavailable",
            HostErrorCode.VaultInUse => "vault_in_use",
            HostErrorCode.InvalidToken => "invalid_token",
            HostErrorCode.InvalidOrigin => "invalid_origin",
            HostErrorCode.InvalidHost => "invalid_host",
            HostErrorCode.AccessDenied => "access_denied",
            HostErrorCode.RateLimited => "rate_limited",
            HostErrorCode.LockInProgress => "lock_in_progress",
            HostErrorCode.ConfigMigrationRequired => "config_migration_required",
            HostErrorCode.MaintenanceInProgress => "maintenance_in_progress",
            _ => "host_error"
        };
    }
}
