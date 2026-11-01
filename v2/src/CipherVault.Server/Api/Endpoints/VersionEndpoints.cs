using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Build;

namespace CipherVault.Server.Api.Endpoints;

public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/version",
                () =>
                {
                    var metadata = BuildMetadataReader.Read();
                    return Results.Ok(new VersionApiResponse(
                        metadata.ProductVersion,
                        metadata.Commit,
                        metadata.Rid,
                        metadata.RuntimeVersion,
                        metadata.AspNetCoreVersion));
                })
            .WithName("GetVersion");
        return endpoints;
    }
}
