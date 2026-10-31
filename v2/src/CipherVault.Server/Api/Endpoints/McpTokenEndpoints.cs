using CipherVault.Server.Mcp;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class McpTokenEndpoints
{
    public static IEndpointRouteBuilder MapMcpTokenEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mcp/token");
        group.MapGet(
                "/status",
                (McpTokenStore tokens) =>
                    Results.Ok(tokens.GetStatus()))
            .RequireAuthorization()
            .WithName("GetMcpTokenStatus");

        group.MapPost(
                "/rotate",
                async (
                    McpTokenStore tokens,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await tokens.RotateAsync(cancellationToken)))
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("RotateMcpToken");
        return endpoints;
    }
}
