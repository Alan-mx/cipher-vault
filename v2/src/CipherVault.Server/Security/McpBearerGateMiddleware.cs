using CipherVault.Server.Api;
using CipherVault.Server.Mcp;

namespace CipherVault.Server.Security;

public sealed class McpBearerGateMiddleware
{
    private readonly RequestDelegate next;
    private readonly McpTokenStore tokenStore;

    public McpBearerGateMiddleware(
        RequestDelegate next,
        McpTokenStore tokenStore)
    {
        this.next = next;
        this.tokenStore = tokenStore;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        var failure = McpBearerTokenReader.Validate(
            context.Request,
            tokenStore);
        if (failure is null)
        {
            context.Items[McpBearerAuthentication.ValidatedItem] = true;
            await next(context);
            return;
        }

        var statusCode =
            failure.Code == ApiErrorCodes.McpTokenNotConfigured
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await ApiResponseWriter.WriteErrorAsync(
            context,
            statusCode,
            failure.Code,
            failure.Code == ApiErrorCodes.McpTokenNotConfigured
                ? "MCP token is not configured."
                : "MCP token is invalid.");
    }
}
