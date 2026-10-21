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
    private readonly string _adminPath;

    public AdminTokenAuthenticationMiddleware(
        RequestDelegate next,
        IAdminTokenStore tokenStore,
        ResolvedCipherVaultHostConfig configuration)
    {
        _next = next;
        _tokenStore = tokenStore;
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

        if (!TryReadBearerToken(context.Request, out var presentedToken))
        {
            await RejectAsync(context);
            return;
        }

        var expectedToken = await _tokenStore.GetTokenAsync(context.RequestAborted);
        if (!TokensEqual(presentedToken, expectedToken))
        {
            await RejectAsync(context);
            return;
        }

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
}
