using CipherVault.Server.Mcp;

namespace CipherVault.Server.Api.Endpoints;

public sealed record McpInfoApiResponse(
    string Transport,
    string EndpointPath,
    bool DraftOnly,
    bool RequiresVaultUnlock,
    IReadOnlyList<McpToolCapabilityApiItem> Tools,
    IReadOnlyList<McpPromptCapabilityApiItem> Prompts);

public sealed record McpToolCapabilityApiItem(
    string Name,
    string Group,
    string Summary);

public sealed record McpPromptCapabilityApiItem(
    string Name,
    string Title,
    string Summary);

public static class McpInfoEndpoints
{
    public static IEndpointRouteBuilder MapMcpInfoEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/mcp/info",
                () => Results.Ok(new McpInfoApiResponse(
                    McpCapabilityCatalog.Transport,
                    McpCapabilityCatalog.EndpointPath,
                    DraftOnly: true,
                    RequiresVaultUnlock: true,
                    McpCapabilityCatalog.Tools
                        .Select(item => new McpToolCapabilityApiItem(
                            item.Name,
                            item.Group,
                            item.Summary))
                        .ToArray(),
                    McpCapabilityCatalog.Prompts
                        .Select(item => new McpPromptCapabilityApiItem(
                            item.Name,
                            item.Title,
                            item.Summary))
                        .ToArray())))
            .RequireAuthorization()
            .WithName("GetMcpInfo");
        return endpoints;
    }
}
