using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;

namespace CipherVault.Host.Http;

internal sealed class AgentTokenAuthenticationMiddleware
{
    public const string ClientIdClaim = "cipher_vault.client_id";
    public const string AccessModeClaim = "cipher_vault.access_mode";
    public const string AuthorizationModeClaim = "cipher_vault.authorization_mode";
    private readonly RequestDelegate _next;
    private readonly AgentTokenStore _tokenStore;
    private readonly HostAuthenticationRateLimiter _rateLimiter;
    private readonly HostAuthenticationKeyedRateLimiter _keyedRateLimiter;
    private readonly ISecurityEventLog _securityLog;
    private readonly HostSessionCoordinator _sessions;
    private readonly string _mcpPath;

    public AgentTokenAuthenticationMiddleware(
        RequestDelegate next,
        AgentTokenStore tokenStore,
        HostAuthenticationRateLimiters rateLimiters,
        ISecurityEventLog securityLog,
        HostSessionCoordinator sessions,
        ResolvedCipherVaultHostConfig configuration)
    {
        _next = next;
        _tokenStore = tokenStore;
        _rateLimiter = rateLimiters.Agent;
        _keyedRateLimiter = rateLimiters.AgentKeys;
        _securityLog = securityLog;
        _sessions = sessions;
        _mcpPath = configuration.Endpoint.McpPath;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(
                _mcpPath,
                StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var hasToken = TryReadBearerToken(context.Request, out var presentedToken);
        var attemptKey = hasToken
            ? ComputeAttemptKey(presentedToken)
            : GetMissingTokenKey(context);
        var globallyBlocked = _rateLimiter.TryGetBlockedUntil(
            out var globalBlockedUntil);
        if (_keyedRateLimiter.TryGetBlockedUntil(
                attemptKey,
                out var keyedBlockedUntil))
        {
            await RejectRateLimitedAsync(context, keyedBlockedUntil).ConfigureAwait(false);
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
            if (globallyBlocked)
            {
                await RejectRateLimitedAsync(context, globalBlockedUntil).ConfigureAwait(false);
            }
            else
            {
                await RejectAsync(context);
            }
            return;
        }

        var client = await _tokenStore.AuthenticateAsync(
            presentedToken,
            context.RequestAborted);
        if (client is null)
        {
            _rateLimiter.RecordFailure();
            _keyedRateLimiter.RecordFailure(attemptKey);
            await LogAsync(
                context,
                "invalid_token",
                "invalid_token").ConfigureAwait(false);
            if (globallyBlocked)
            {
                await RejectRateLimitedAsync(context, globalBlockedUntil).ConfigureAwait(false);
            }
            else
            {
                await RejectAsync(context);
            }
            return;
        }

        _keyedRateLimiter.RecordSuccess(attemptKey);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClientIdClaim, client.ClientId),
                new Claim(
                    AccessModeClaim,
                    AgentTokenStore.ToAccessMode(client.AccessMode)),
                new Claim(
                    AuthorizationModeClaim,
                    AgentTokenStore.ToAuthorizationMode(client.AuthorizationMode))
            ],
            authenticationType: "CipherVaultAgentToken");
        context.User = new ClaimsPrincipal(identity);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        using var connection = _sessions.EnterClient();
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
            "A valid agent token is required.");
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
