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
            || (origins.Count == 1
                && !IsAllowedOrigin(origins[0])))
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
        try
        {
            var value = host.ToUriComponent();
            if (string.IsNullOrWhiteSpace(value)
                || !Uri.TryCreate(
                    $"http://{value}",
                    UriKind.Absolute,
                    out var uri)
                || !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            var hostName = uri.DnsSafeHost;
            var port = uri.IsDefaultPort ? 80 : uri.Port;
            return !string.IsNullOrWhiteSpace(hostName)
                && !hostName.EndsWith(".", StringComparison.Ordinal)
                && port is >= 1 and <= 65535
                && options.AllowedHttpPorts.Contains(port)
                && options.AllowedHosts.Contains(
                    hostName,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is FormatException
                or ArgumentException
                or UriFormatException)
        {
            return false;
        }
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

        var host = uri.DnsSafeHost;
        var port = uri.IsDefaultPort ? 80 : uri.Port;
        if (string.IsNullOrWhiteSpace(host)
            || host.EndsWith(".", StringComparison.Ordinal)
            || port is < 1 or > 65535)
        {
            return false;
        }

        if (options.DevelopmentOrigins.Any(configured =>
                MatchesAuthority(configured, host, port)))
        {
            return true;
        }

        return options.AllowedHttpPorts.Contains(port)
            && options.AllowedHosts.Contains(
                host,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesAuthority(
        Uri configured,
        string host,
        int port) =>
        string.Equals(
            configured.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(configured.UserInfo)
        && configured.AbsolutePath == "/"
        && string.IsNullOrEmpty(configured.Query)
        && string.IsNullOrEmpty(configured.Fragment)
        && string.Equals(
            configured.Host,
            host,
            StringComparison.OrdinalIgnoreCase)
        && (configured.IsDefaultPort ? 80 : configured.Port) == port;
}
