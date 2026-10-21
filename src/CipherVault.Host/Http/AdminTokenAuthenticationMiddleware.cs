using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;
using CipherVault.Host.Client.Security;
using CipherVault.Host.Security;

namespace CipherVault.Host.Http;

internal sealed class AdminTokenAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAdminTokenStore _tokenStore;
    private readonly HostAuthenticationRateLimiter _rateLimiter;
    private readonly HostAuthenticationKeyedRateLimiter _keyedRateLimiter;
    private readonly ISecurityEventLog _securityLog;
    private readonly string _adminPath;
    private readonly string _identityPath;

    public AdminTokenAuthenticationMiddleware(
        RequestDelegate next,
        IAdminTokenStore tokenStore,
        HostAuthenticationRateLimiters rateLimiters,
        ISecurityEventLog securityLog,
        ResolvedCipherVaultHostConfig configuration)
    {
        _next = next;
        _tokenStore = tokenStore;
        _rateLimiter = rateLimiters.Admin;
        _keyedRateLimiter = rateLimiters.AdminKeys;
        _securityLog = securityLog;
        _adminPath = configuration.Endpoint.AdminPath;
        _identityPath = configuration.Endpoint.AdminPath + "/identity";
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

        if (string.Equals(
                context.Request.Path.Value,
                _identityPath,
                StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store";
            await _next(context);
            return;
        }

        var hasToken = TryReadBearerToken(context.Request, out var presentedToken);
        var attemptKey = hasToken
            ? ComputeAttemptKey(presentedToken)
            : GetMissingTokenKey(context);
        if (_rateLimiter.TryGetBlockedUntil(out var blockedUntil))
        {
            await RejectRateLimitedAsync(context, blockedUntil).ConfigureAwait(false);
            return;
        }
        if (_keyedRateLimiter.TryGetBlockedUntil(
                attemptKey,
                out blockedUntil))
        {
            await RejectRateLimitedAsync(context, blockedUntil).ConfigureAwait(false);
            return;
        }

        if (!hasToken)
        {
            _rateLimiter.RecordFailure();
            _keyedRateLimiter.RecordFailure(attemptKey);
            await LogAsync(
                context,
                "invalid_token",
                "missing_token").ConfigureAwait(false);
            await RejectAsync(context);
            return;
        }

        var clientKind = await _tokenStore.AuthenticateAsync(
            presentedToken,
            context.RequestAborted);
        if (clientKind is null)
        {
            _rateLimiter.RecordFailure();
            _keyedRateLimiter.RecordFailure(attemptKey);
            await LogAsync(
                context,
                "invalid_token",
                "invalid_token").ConfigureAwait(false);
            await RejectAsync(context);
            return;
        }

        _keyedRateLimiter.RecordSuccess(attemptKey);
        context.Items[HostAdminContext.ActorItem] =
            clientKind == HostAdminClientKind.Cli
                ? VaultActorKind.UserCli
                : VaultActorKind.UserUi;
        context.Items[HostHttpContextKeys.AdminAuthenticated] = true;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        await _next(context);
    }

    private static string ComputeAttemptKey(string token)
    {
        return "token:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string GetMissingTokenKey(HttpContext context)
    {
        return "missing:" +
            (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private async Task RejectRateLimitedAsync(
        HttpContext context,
        DateTimeOffset blockedUntil)
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
        return SecurityLogSafety.TryWriteAsync(
            _securityLog,
            new HostSecurityLogEvent(
                DateTimeOffset.UtcNow,
                eventType,
                ClientId: null,
                ConnectionId: null,
                ClientInstanceId: null,
                GrantId: null,
                GrantScopes: null,
                requestId,
                "denied",
                reasonCode),
            context.RequestAborted);
    }
}
