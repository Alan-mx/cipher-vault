using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Http;

internal sealed class AdminTokenAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAdminTokenStore _tokenStore;
    private readonly HostAuthenticationRateLimiter _rateLimiter;
    private readonly ISecurityEventLog _securityLog;
    private readonly string _adminPath;

    public AdminTokenAuthenticationMiddleware(
        RequestDelegate next,
        IAdminTokenStore tokenStore,
        HostAuthenticationRateLimiter rateLimiter,
        ISecurityEventLog securityLog,
        ResolvedCipherVaultHostConfig configuration)
    {
        _next = next;
        _tokenStore = tokenStore;
        _rateLimiter = rateLimiter;
        _securityLog = securityLog;
        _adminPath = configuration.Endpoint.AdminPath;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(
                _adminPath,
                StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (_rateLimiter.TryGetBlockedUntil(out var blockedUntil))
        {
            await LogAsync(
                context,
                "rate_limited",
                "rate_limited").ConfigureAwait(false);
            context.Response.Headers.RetryAfter = Math.Max(
                1,
                (int)Math.Ceiling((blockedUntil - DateTimeOffset.UtcNow).TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            await HostHttpResponses.WriteErrorAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                HostErrorCode.RateLimited,
                "Too many authentication failures. Try again later.");
            return;
        }

        if (!TryReadBearerToken(context.Request, out var presentedToken))
        {
            _rateLimiter.RecordFailure();
            await LogAsync(
                context,
                "invalid_token",
                "missing_token").ConfigureAwait(false);
            await RejectAsync(context);
            return;
        }

        var expectedToken = await _tokenStore.GetTokenAsync(context.RequestAborted);
        if (!TokensEqual(presentedToken, expectedToken))
        {
            _rateLimiter.RecordFailure();
            await LogAsync(
                context,
                "invalid_token",
                "invalid_token").ConfigureAwait(false);
            await RejectAsync(context);
            return;
        }

        _rateLimiter.RecordSuccess();
        context.Items[HostHttpContextKeys.AdminAuthenticated] = true;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        await _next(context);
    }

    private static bool TryReadBearerToken(
        HttpRequest request,
        out string token)
    {
        token = string.Empty;
        if (!AuthenticationHeaderValue.TryParse(
                request.Headers.Authorization.ToString(),
                out var authorization) ||
            !string.Equals(
                authorization.Scheme,
                "Bearer",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return false;
        }
        token = authorization.Parameter;
        return true;
    }

    private static bool TokensEqual(string presented, string expected)
    {
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                presentedBytes,
                expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presentedBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return HostHttpResponses.WriteErrorAsync(
            context,
            StatusCodes.Status401Unauthorized,
            HostErrorCode.InvalidToken,
            "A valid admin token is required.");
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
