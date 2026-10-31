using CipherVault.Core.Errors;
using CipherVault.Server.Api;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Middleware;

public sealed class HostOriginValidationMiddleware
{
    private readonly RequestDelegate next;
    private readonly ServerOptions options;

    public HostOriginValidationMiddleware(
        RequestDelegate next,
        ServerOptions options)
    {
        this.next = next;
        this.options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAllowedHost(context.Request.Host))
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.HostInvalid,
                "Host header is not allowed.");
            return;
        }

        var origins = context.Request.Headers.Origin;
        if (origins.Count > 1
            || (origins.Count == 1 && !IsAllowedOrigin(origins[0])))
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.OriginInvalid,
                "Request origin is not allowed.");
            return;
        }

        await next(context);
    }

    private bool IsAllowedHost(HostString host)
    {
        var port = host.Port ?? 80;
        var portAllowed = options.ContainerMode
            || options.ListenAddresses.Any(address => address.Port == port);
        return portAllowed
            && options.AllowedHosts.Contains(
                host.Host,
                StringComparer.OrdinalIgnoreCase);
    }

    private bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var portAllowed = options.ContainerMode
            || options.ListenAddresses.Any(address => address.Port == uri.Port);
        return portAllowed
            && options.AllowedHosts.Contains(
                uri.Host,
                StringComparer.OrdinalIgnoreCase);
    }
}
