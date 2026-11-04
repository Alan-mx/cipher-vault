using CipherVault.Server.Security;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CipherVault.Server.Mcp;

public static class McpServerRegistration
{
    public static IServiceCollection AddCipherVaultMcp(
        this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<McpToolInvoker>();
        services.AddAuthorizationBuilder()
            .AddPolicy(McpBearerAuthentication.Policy, policy =>
            {
                policy.AddAuthenticationSchemes(McpBearerAuthentication.Scheme);
                policy.RequireAuthenticatedUser();
            });
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpBearerAuthenticationHandler>(
                McpBearerAuthentication.Scheme,
                _ => { });
        services.AddMcpServer(options =>
            {
                options.ServerInstructions =
                    """
                    CipherVault defaults to Draft Mode. Formal document write tools are always listed,
                    but return write_mode_draft_required until the user enables Direct Write Mode in
                    the Web UI. Prefer draft tools for create, update, and trash proposals. MCP cannot
                    approve, reject, or apply drafts. Use a unique UUID idempotencyKey for every
                    side-effecting call and never retry the same key with a different request.
                    """;
            })
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithToolsFromAssembly(typeof(McpServerRegistration).Assembly);
        return services;
    }

    public static IEndpointConventionBuilder MapCipherVaultMcp(
        this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp("/mcp")
            .RequireAuthorization(McpBearerAuthentication.Policy);
}
