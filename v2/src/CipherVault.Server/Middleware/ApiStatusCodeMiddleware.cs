using CipherVault.Server.Api;

namespace CipherVault.Server.Middleware;

public sealed class ApiStatusCodeMiddleware
{
    private readonly RequestDelegate next;

    public ApiStatusCodeMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        if (!context.Request.Path.StartsWithSegments("/api")
            || context.Response.HasStarted
            || context.Response.StatusCode < 400
            || context.Response.ContentLength is not null
            || context.Response.ContentType is not null)
        {
            return;
        }

        var (code, message) = context.Response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => (
                ApiErrorCodes.AuthenticationRequired,
                "Authentication is required."),
            StatusCodes.Status403Forbidden => (
                ApiErrorCodes.Forbidden,
                "This operation is not permitted."),
            StatusCodes.Status404NotFound => (
                ApiErrorCodes.RouteNotFound,
                "API route was not found."),
            StatusCodes.Status405MethodNotAllowed => (
                ApiErrorCodes.MethodNotAllowed,
                "HTTP method is not allowed for this route."),
            StatusCodes.Status415UnsupportedMediaType => (
                ApiErrorCodes.UnsupportedMediaType,
                "Content type is not supported."),
            _ => (
                ApiErrorCodes.InvalidRequest,
                "Request is invalid.")
        };
        await ApiResponseWriter.WriteErrorAsync(
            context,
            context.Response.StatusCode,
            code,
            message);
    }
}
