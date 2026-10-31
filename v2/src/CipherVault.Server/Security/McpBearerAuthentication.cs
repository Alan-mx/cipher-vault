using System.Security.Claims;
using System.Text.Encodings.Web;
using CipherVault.Server.Api;
using CipherVault.Server.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace CipherVault.Server.Security;

public static class McpBearerAuthentication
{
    public const string Scheme = "CipherVault.McpBearer";
    public const string Policy = "CipherVault.McpBearerPolicy";
    internal const string ValidatedItem = "CipherVault.McpBearerValidated";
}

internal sealed record McpAuthFailure(string Code);

internal static class McpBearerTokenReader
{
    internal static McpAuthFailure? Validate(
        HttpRequest request,
        McpTokenStore tokenStore)
    {
        if (!tokenStore.GetStatus().Configured)
        {
            return new McpAuthFailure(
                ApiErrorCodes.McpTokenNotConfigured);
        }

        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1)
        {
            return new McpAuthFailure(ApiErrorCodes.InvalidToken);
        }

        var value = authorization[0];
        if (string.IsNullOrEmpty(value)
            || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new McpAuthFailure(ApiErrorCodes.InvalidToken);
        }

        var token = value["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token)
            || token.Length > 1024
            || !tokenStore.TryAuthenticate(token))
        {
            return new McpAuthFailure(ApiErrorCodes.InvalidToken);
        }

        return null;
    }
}

public sealed class McpBearerAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";
    internal const string FailureItem = "CipherVault.McpAuthFailure";

    private readonly McpTokenStore tokenStore;

    public McpBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        McpTokenStore tokenStore)
        : base(options, logger, encoder)
    {
        this.tokenStore = tokenStore;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Items.ContainsKey(McpBearerAuthentication.ValidatedItem))
        {
            return Task.FromResult(CreateSuccess());
        }

        var failure = McpBearerTokenReader.Validate(
            Request,
            tokenStore);
        if (failure is not null)
        {
            return Task.FromResult(Fail(failure.Code));
        }

        return Task.FromResult(CreateSuccess());
    }

    private AuthenticateResult CreateSuccess()
    {
        var identity = new ClaimsIdentity(
            claims: [],
            authenticationType: McpBearerAuthentication.Scheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            McpBearerAuthentication.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items.TryGetValue(FailureItem, out var value)
            && value is McpAuthFailure failure
            ? failure.Code
            : tokenStore.GetStatus().Configured
                ? ApiErrorCodes.InvalidToken
                : ApiErrorCodes.McpTokenNotConfigured;
        var statusCode = code == ApiErrorCodes.McpTokenNotConfigured
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await ApiResponseWriter.WriteErrorAsync(
            Context,
            statusCode,
            code,
            code == ApiErrorCodes.McpTokenNotConfigured
                ? "MCP token is not configured."
                : "MCP token is invalid.",
            retryAfterSeconds: statusCode == StatusCodes.Status503ServiceUnavailable ? 5 : null);
    }

    private AuthenticateResult Fail(string code)
    {
        Context.Items[FailureItem] = new McpAuthFailure(code);
        return AuthenticateResult.Fail("MCP token validation failed.");
    }
}
