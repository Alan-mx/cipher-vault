using CipherVault.Core.Configuration;
using CipherVault.Core.Session;
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
        app.MapPost(
            configuration.Endpoint.AdminPath + "/host/stop",
            async (
                HostStopRequest request,
                HostSessionCoordinator sessions,
                IHostApplicationLifetime lifetime,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var reason = string.IsNullOrWhiteSpace(request.Reason)
                    ? "user-requested"
                    : request.Reason.Trim();
                if (reason.Length > 200)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        HostErrorCode.InvalidInput,
                        "Stop reason is too long.");
                    return Results.Empty;
                }

                var snapshot = await sessions.LockAsync(reason, cancellationToken);
                if (snapshot.State != VaultSessionState.Locked)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status409Conflict,
                        HostErrorCode.LockInProgress,
                        "Vault lock is still in progress.");
                    return Results.Empty;
                }

                context.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
                return Results.NoContent();
            });
        return app;
    }
}
