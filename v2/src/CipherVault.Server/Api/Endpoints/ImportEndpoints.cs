using CipherVault.Application.Import;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/imports");
        group.MapPost(
                "/preview",
                async (
                    ImportPreviewRequest request,
                    ImportService imports,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await imports.PreviewAsync(
                        request,
                        context.CreateUserActor(),
                        cancellationToken)))
            .RequireAuthorization()
            .RequireLoopbackRemote()
            .RequireAntiforgery()
            .WithName("PreviewImport");

        group.MapPost(
                "/commit",
                async (
                    ImportCommitRequest request,
                    ImportService imports,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await imports.CommitAsync(
                        request,
                        context.CreateUserActor(),
                        cancellationToken)))
            .RequireAuthorization()
            .RequireLoopbackRemote()
            .RequireAntiforgery()
            .WithName("CommitImport");
        return endpoints;
    }
}
