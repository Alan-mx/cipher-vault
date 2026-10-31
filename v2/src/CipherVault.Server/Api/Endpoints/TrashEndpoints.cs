using CipherVault.Application.Documents;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Api.Endpoints;

public static class TrashEndpoints
{
    public static IEndpointRouteBuilder MapTrashEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/trash");
        group.MapGet(
                "",
                async (
                    int? pageSize,
                    string? cursor,
                    TrashService trash,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await trash.ListAsync(
                        new CursorPageRequest(pageSize, cursor),
                        cancellationToken)))
            .RequireAuthorization()
            .WithName("ListTrash");

        group.MapPost(
                "/{id}/restore",
                async (
                    string id,
                    HttpRequest request,
                    TrashService trash,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var body = await ApiRequestBinding.ReadOptionalJsonAsync<
                        TrashRestoreApiRequest>(request, cancellationToken);
                    var result = await trash.RestoreAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        context.GetExpectedRevision(),
                        body?.ReplacementPath,
                        context.CreateUserActor(),
                        cancellationToken);
                    context.Response.SetRevisionETag(result.Revision);
                    return Results.Ok(result);
                })
            .RequireAuthorization()
            .RequireRevisionETag()
            .RequireAntiforgery()
            .WithName("RestoreTrashDocument");

        group.MapPost(
                "/{id}/purge",
                async (
                    string id,
                    TrashService trash,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var auditEventId = await trash.PurgeAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        context.GetExpectedRevision(),
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(new PurgeApiResult(auditEventId));
                })
            .RequireAuthorization()
            .RequireRevisionETag()
            .RequireAntiforgery()
            .WithName("PurgeTrashDocument");
        return endpoints;
    }
}
