using CipherVault.Application.Documents;
using CipherVault.Server.Security;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Api.Endpoints;

public static class RevisionEndpoints
{
    public static IEndpointRouteBuilder MapRevisionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/documents/{id}/revisions");
        group.MapGet(
                "",
                async (
                    string id,
                    int? pageSize,
                    string? cursor,
                    DocumentRevisionService revisions,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await revisions.ListAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        new CursorPageRequest(pageSize, cursor),
                        cancellationToken)))
            .RequireAuthorization()
            .WithName("ListDocumentRevisions");

        group.MapGet(
                "/{revisionId}/content",
                async (
                    string id,
                    string revisionId,
                    DocumentRevisionService revisions,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await revisions.GetContentAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        ApiQueryBinding.ParseGuid(revisionId, "revisionId"),
                        cancellationToken)))
            .RequireAuthorization()
            .WithName("GetDocumentRevisionContent");

        group.MapPost(
                "/{revisionId}/restore",
                async (
                    string id,
                    string revisionId,
                    DocumentRevisionService revisions,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var result = await revisions.RestoreAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        ApiQueryBinding.ParseGuid(revisionId, "revisionId"),
                        context.GetExpectedRevision(),
                        context.CreateUserActor(),
                        cancellationToken);
                    context.Response.SetRevisionETag(result.Revision);
                    return Results.Ok(result);
                })
            .RequireAuthorization()
            .RequireRevisionETag()
            .RequireAntiforgery()
            .WithName("RestoreDocumentRevision");
        return endpoints;
    }
}
