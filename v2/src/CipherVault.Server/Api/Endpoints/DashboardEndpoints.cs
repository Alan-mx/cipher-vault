using CipherVault.Application.KnowledgeBase;

namespace CipherVault.Server.Api.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/dashboard",
                async (
                    DashboardService dashboard,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await dashboard.GetAsync(cancellationToken)))
            .RequireAuthorization()
            .WithName("GetDashboard");
        return endpoints;
    }
}
