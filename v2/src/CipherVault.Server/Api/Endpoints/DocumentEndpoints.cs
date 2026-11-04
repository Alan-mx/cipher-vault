using CipherVault.Application.Documents;
using CipherVault.Application.RelatedKnowledge;
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
                    string[]? tags,
                    bool? orphanOnly,
                    int? pageSize,
                    string? cursor,
                    DocumentService documents,
                    CancellationToken cancellationToken) =>
                {
                    var query = new CatalogQuery(
                        folder,
                        ApiQueryBinding.ParseFormat(format),
                        new CursorPageRequest(pageSize, cursor),
                        tags,
                        orphanOnly ?? false);
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

        group.MapPost(
                "/{id}/copy",
                async (
                    string id,
                    CopyDocumentApiRequest request,
                    DocumentService documents,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await documents.CopyIdempotentAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        context.GetExpectedRevision(),
                        new CopyDocumentRequest(
                            request.Title,
                            request.VirtualPath),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    context.Response.SetRevisionETag(result.Revision);
                    context.Response.Headers.Location =
                        $"/api/documents/{result.DocumentId:D}";
                    return Results.Created(
                        context.Response.Headers.Location,
                        result);
                })
            .RequireAuthorization()
            .RequireRevisionETag()
            .RequireAntiforgery()
            .WithName("CopyDocument");

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
                    HttpRequest request,
                    DocumentAuthoringService authoring,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var documentId = ApiQueryBinding.ParseGuid(id, "id");
                    var body = await ApiRequestBinding.ReadRequiredJsonElementAsync(
                        request,
                        cancellationToken);
                    var update = UpdateDocumentApiRequest.Parse(body);
                    var result = await authoring.UpdatePartialAsync(
                        documentId,
                        context.GetExpectedRevision(),
                        update.ToApplicationUpdate(),
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

        group.MapGet(
                "/{id}/related",
                async (
                    string id,
                    int? limit,
                    RelatedKnowledgeService related,
                    CancellationToken cancellationToken) =>
                {
                    var items = await related.GetAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        limit ?? 10,
                        cancellationToken);
                    return Results.Ok(new RelatedDocumentsResponse(items));
                })
            .RequireAuthorization()
            .WithName("ListRelatedDocuments");
        return endpoints;
    }
}
