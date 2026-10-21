using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Http;
using CipherVault.Host.Security;
using CipherVault.Host.Session;

namespace CipherVault.Host;

public static class HostApplication
{
    public const string ProtocolVersion = "1";

    public static WebApplication Configure(
        WebApplicationBuilder builder,
        ResolvedCipherVaultHostConfig configuration,
        IAdminTokenStore adminTokenStore)
    {
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(adminTokenStore);
        builder.Services.AddSingleton<HostSessionCoordinator>();

        var app = builder.Build();
        app.UseMiddleware<HostRequestValidationMiddleware>();
        app.UseMiddleware<AdminTokenAuthenticationMiddleware>();
        app.MapGet(
            configuration.Endpoint.AdminPath + "/health",
            () => Results.Json(
                new HostApiResponse<HostHealthData>(
                    true,
                    new HostHealthData(
                        "ready",
                        typeof(HostApplication).Assembly.GetName().Version?.ToString()
                            ?? "1.0.0",
                        ProtocolVersion))));
        app.MapGet(
            configuration.Endpoint.AdminPath + "/status",
            (HostSessionCoordinator sessions) =>
            {
                var snapshot = sessions.GetSnapshot();
                return Results.Json(
                    new HostApiResponse<HostStatusData>(
                        true,
                        new HostStatusData(
                            "ready",
                            typeof(HostApplication).Assembly.GetName().Version?.ToString()
                                ?? "1.0.0",
                            ProtocolVersion,
                            new HostEndpointStatusData(
                                $"http://{configuration.Endpoint.BindAddress}:{configuration.Endpoint.Port}",
                                configuration.Endpoint.McpPath,
                                configuration.Endpoint.AdminPath),
                            new HostSessionStatusData(
                                snapshot.State.ToString().ToLowerInvariant(),
                                snapshot.SessionId,
                                snapshot.StartedAt,
                                snapshot.LastUserActivityAt,
                                snapshot.IdleExpiresAt,
                                snapshot.HardExpiresAt,
                                snapshot.LockReason,
                                snapshot.ActiveLeases,
                                snapshot.ConnectedClients))));
            });
        return app;
    }
}
