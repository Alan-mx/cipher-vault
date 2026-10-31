using CipherVault.Application.Documents;
using CipherVault.Server.Api.Contracts;
using CipherVault.Core.Documents;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/documents");
        group.MapGet(
                "",
                async (
                    string? folder,
                    string? format,
                    int? pageSize,
                    string? cursor,
                    DocumentService documents,
                    CancellationToken cancellationToken) =>
                {
                    var query = new CatalogQuery(
                        folder,
                        ApiQueryBinding.ParseFormat(format),
                        new CursorPageRequest(pageSize, cursor));
                    return Results.Ok(await documents.ListAsync(
                        query,
                        cancellationToken));
                })
            .RequireAuthorization()
            .WithName("ListDocuments");

        group.MapPost(
                "",
                async (
                    CreateDocumentApiRequest request,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var result = await documents.CreateAsync(
                        request.ToApplicationRequest(),
                        context.CreateUserActor(),
                        cancellationToken);
                    context.Response.SetRevisionETag(result.Revision);
                    context.Response.Headers.Location =
                        $"/api/documents/{result.DocumentId:D}";
                    return Results.Created(
                        context.Response.Headers.Location,
                        result);
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("CreateDocument");

        group.MapGet(
                "/{id}",
                async (
                    string id,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    var metadata = await documents.GetAsync(
                        documentId,
                        cancellationToken);
                    context.Response.SetRevisionETag(metadata.Revision);
                    return Results.Ok(metadata);
                })
            .RequireAuthorization()
            .WithName("GetDocumentMetadata");

        group.MapGet(
                "/{id}/content",
                async (
                    string id,
                    int? offset,
                    int? maxChars,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    var chunk = await documents.ReadContentAsync(
                        documentId,
                        offset ?? 0,
                        maxChars ?? 32_768,
                        cancellationToken);
                    context.Response.SetRevisionETag(chunk.Revision);
                    return Results.Ok(chunk);
                })
            .RequireAuthorization()
            .WithName("ReadDocumentContent");

        group.MapGet(
                "/{id}/map",
                async (
                    string id,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    var map = await documents.GetMapAsync(
                        documentId,
                        cancellationToken);
                    context.Response.SetRevisionETag(map.Metadata.Revision);
                    return Results.Ok(map);
                })
            .RequireAuthorization()
            .WithName("GetDocumentMap");

        group.MapPut(
                "/{id}",
                async (
                    string id,
                    UpdateDocumentApiRequest request,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    var result = await documents.UpdateAsync(
                        documentId,
                        context.GetExpectedRevision(),
                        request.ToApplicationRequest(),
                        context.CreateUserActor(),
                        cancellationToken);
                    context.Response.SetRevisionETag(result.Revision);
                    return Results.Ok(result);
                })
            .RequireAuthorization()
            .RequireRevisionETag()
            .RequireAntiforgery()
            .WithName("UpdateDocument");

        group.MapPatch(
                "/{id}",
                async (
                    string id,
                    PatchDocumentApiRequest request,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    var result = await documents.PatchAsync(
                        documentId,
                        context.GetExpectedRevision(),
                        request.ToApplicationCommand(),
                        context.CreateUserActor(),
                        cancellationToken);
                    context.Response.SetRevisionETag(result.Revision);
                    return Results.Ok(result);
                })
            .RequireAuthorization()
            .RequireRevisionETag()
            .RequireAntiforgery()
            .WithName("PatchDocument");

        group.MapDelete(
                "/{id}",
                async (
                    string id,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    var result = await documents.DeleteAsync(
                        documentId,
                        context.GetExpectedRevision(),
                        context.CreateUserActor(),
                        cancellationToken);
                    context.Response.SetRevisionETag(result.Revision);
                    return Results.Ok(result);
                })
            .RequireAuthorization()
            .RequireRevisionETag()
            .RequireAntiforgery()
            .WithName("DeleteDocument");

        group.MapGet(
                "/{id}/backlinks",
                async (
                    string id,
                    int? pageSize,
                    string? cursor,
                    DocumentService documents,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    return Results.Ok(await documents.ListBacklinksAsync(
                        documentId,
                        new CursorPageRequest(pageSize, cursor),
                        cancellationToken));
                })
            .RequireAuthorization()
            .WithName("ListDocumentBacklinks");

        group.MapGet(
                "/{id}/unlinked-mentions",
                async (
                    string id,
                    int? pageSize,
                    string? cursor,
                    DocumentService documents,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    return Results.Ok(await documents.ListUnlinkedMentionsAsync(
                        documentId,
                        new CursorPageRequest(pageSize, cursor),
                        cancellationToken));
                })
            .RequireAuthorization()
            .WithName("ListDocumentUnlinkedMentions");
        return endpoints;
    }
}
