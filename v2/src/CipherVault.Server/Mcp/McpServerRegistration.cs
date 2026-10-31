using CipherVault.Server.Security;
using ModelContextProtocol.AspNetCore;
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
        services.AddMcpServer()
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
