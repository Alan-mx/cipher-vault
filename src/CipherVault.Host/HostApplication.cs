using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;
using CipherVault.Host.Http;
using CipherVault.Host.Security;

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

        var app = builder.Build();
        app.UseMiddleware<HostRequestValidationMiddleware>();
        app.UseMiddleware<AdminTokenAuthenticationMiddleware>();
        app.MapGet(
            configuration.Endpoint.AdminPath + "/health",
            (HttpContext context) => Results.Json(
                new HostApiResponse<HostHealthData>(
                    true,
                    new HostHealthData(
                        "ready",
                        typeof(HostApplication).Assembly.GetName().Version?.ToString()
                            ?? "1.0.0",
                        ProtocolVersion))));
        return app;
    }
}
