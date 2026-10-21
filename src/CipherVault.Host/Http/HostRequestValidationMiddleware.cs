using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Http;

internal sealed class HostRequestValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HostEndpointOptions _endpoint;

    public HostRequestValidationMiddleware(
        RequestDelegate next,
        ResolvedCipherVaultHostConfig configuration)
    {
        _next = next;
        _endpoint = configuration.Endpoint;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers["X-Request-Id"].ToString();
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
        {
            requestId = Guid.NewGuid().ToString("D");
        }
        context.Items[HostHttpContextKeys.RequestId] = requestId;
        context.Response.Headers["X-Request-Id"] = requestId;

        var expectedHost = $"{_endpoint.BindAddress}:{_endpoint.Port}";
        if (!string.Equals(
                context.Request.Host.Value,
                expectedHost,
                StringComparison.OrdinalIgnoreCase))
        {
            await HostHttpResponses.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                HostErrorCode.InvalidHost,
                "Request Host header is not allowed.");
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            !_endpoint.AllowedOrigins.Contains(origin))
        {
            await HostHttpResponses.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                HostErrorCode.InvalidOrigin,
                "Request Origin is not allowed.");
            return;
        }

        await _next(context);
    }
}
