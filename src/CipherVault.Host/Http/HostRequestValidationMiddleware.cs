using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using Microsoft.AspNetCore.Http.Features;

namespace CipherVault.Host.Http;

internal sealed class HostRequestValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HostEndpointOptions _endpoint;
    private readonly ISecurityEventLog _securityLog;

    public HostRequestValidationMiddleware(
        RequestDelegate next,
        ResolvedCipherVaultHostConfig configuration,
        ISecurityEventLog securityLog)
    {
        _next = next;
        _endpoint = configuration.Endpoint;
        _securityLog = securityLog;
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
            await LogAsync(
                context,
                "invalid_host",
                "invalid_host").ConfigureAwait(false);
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
            await LogAsync(
                context,
                "invalid_origin",
                "invalid_origin").ConfigureAwait(false);
            await HostHttpResponses.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                HostErrorCode.InvalidOrigin,
                "Request Origin is not allowed.");
            return;
        }

        if (context.Request.ContentLength is > 0 &&
            context.Request.ContentLength > _endpoint.MaxRequestBodyBytes)
        {
            await LogAsync(
                context,
                "request_too_large",
                "content_length").ConfigureAwait(false);
            await HostHttpResponses.WriteErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                HostErrorCode.RequestTooLarge,
                "Request body exceeds the configured limit.");
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is
            { IsReadOnly: false } bodySizeFeature)
        {
            bodySizeFeature.MaxRequestBodySize = _endpoint.MaxRequestBodyBytes;
        }

        await _next(context);
    }

    private Task LogAsync(
        HttpContext context,
        string eventType,
        string reasonCode)
    {
        var requestId = context.Items.TryGetValue(
                HostHttpContextKeys.RequestId,
                out var value)
            ? value as string
            : null;
        return _securityLog.WriteAsync(
            new HostSecurityLogEvent(
                DateTimeOffset.UtcNow,
                eventType,
                ClientId: null,
                ConnectionId: null,
                requestId,
                "denied",
                reasonCode),
            context.RequestAborted);
    }
}
