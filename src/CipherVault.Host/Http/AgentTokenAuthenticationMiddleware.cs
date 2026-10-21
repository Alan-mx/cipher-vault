using System.Net.Http.Headers;
using System.Security.Claims;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Http;

internal sealed class AgentTokenAuthenticationMiddleware
{
    public const string ClientIdClaim = "cipher_vault.client_id";
    public const string AccessModeClaim = "cipher_vault.access_mode";
    public const string AllowLockClaim = "cipher_vault.allow_lock";
    private readonly RequestDelegate _next;
    private readonly AgentTokenStore _tokenStore;
    private readonly string _mcpPath;

    public AgentTokenAuthenticationMiddleware(
        RequestDelegate next,
        AgentTokenStore tokenStore,
        ResolvedCipherVaultHostConfig configuration)
    {
        _next = next;
        _tokenStore = tokenStore;
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

        if (!TryReadBearerToken(context.Request, out var presentedToken))
        {
            await RejectAsync(context);
            return;
        }

        var client = await _tokenStore.AuthenticateAsync(
            presentedToken,
            context.RequestAborted);
        if (client is null)
        {
            await RejectAsync(context);
            return;
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClientIdClaim, client.ClientId),
                new Claim(
                    AccessModeClaim,
                    AgentTokenStore.ToAccessMode(client.AccessMode)),
                new Claim(AllowLockClaim, client.AllowLock ? "true" : "false")
            ],
            authenticationType: "CipherVaultAgentToken");
        context.User = new ClaimsPrincipal(identity);
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

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return HostHttpResponses.WriteErrorAsync(
            context,
            StatusCodes.Status401Unauthorized,
            HostErrorCode.InvalidToken,
            "A valid agent token is required.");
    }
}
