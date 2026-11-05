using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class KnowledgeBaseEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeBaseEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var knowledge = endpoints
            .MapGroup("/api/knowledge")
            .RequireAuthorization();
        knowledge.MapGet(
                "",
                async (
                    string? status,
                    string? typeId,
                    string[]? tags,
                    int? pageSize,
                    string? cursor,
                    KnowledgeService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListAsync(
                        new KnowledgeQuery(
                            ParseStatus(status),
                            typeId is null
                                ? null
                                : ApiQueryBinding.ParseGuid(typeId, "typeId"),
                            tags,
                            pageSize ?? 20,
                            cursor),
                        cancellationToken)))
            .WithName("ListKnowledge");

        knowledge.MapPost(
                "/search",
                async (
                    KnowledgeSearchApiRequest request,
                    KnowledgeService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.SearchAsync(
                        request.ToDomain(),
                        cancellationToken)))
            .WithName("SearchKnowledge");

        knowledge.MapGet(
                "/{knowledgeId}",
                async (
                    string knowledgeId,
                    KnowledgeService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var detail = await service.GetAsync(
                        ApiQueryBinding.ParseGuid(knowledgeId, "knowledgeId"),
                        cancellationToken);
                    context.Response.SetRevisionETag(detail.CurrentRevision);
                    return Results.Ok(detail);
                })
            .WithName("GetKnowledge");

        knowledge.MapGet(
                "/{knowledgeId}/revisions",
                async (
                    string knowledgeId,
                    int? pageSize,
                    string? cursor,
                    KnowledgeService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListRevisionsAsync(
                        ApiQueryBinding.ParseGuid(knowledgeId, "knowledgeId"),
                        pageSize ?? 20,
                        cursor,
                        cancellationToken)))
            .WithName("ListKnowledgeRevisions");

        knowledge.MapGet(
                "/{knowledgeId}/revisions/{revision}/content",
                async (
                    string knowledgeId,
                    int revision,
                    int? offset,
                    int? maxChars,
                    KnowledgeService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var chunk = await service.ReadContentAsync(
                        ApiQueryBinding.ParseGuid(knowledgeId, "knowledgeId"),
                        revision,
                        offset ?? 0,
                        maxChars ?? 32_768,
                        cancellationToken);
                    context.Response.SetRevisionETag(chunk.KnowledgeRevision);
                    return Results.Ok(chunk);
                })
            .WithName("ReadKnowledgeContent");

        knowledge.MapGet(
                "/{knowledgeId}/revisions/{revision}/document",
                async (
                    string knowledgeId,
                    int revision,
                    KnowledgeService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ExportDocumentAsync(
                        ApiQueryBinding.ParseGuid(knowledgeId, "knowledgeId"),
                        revision,
                        cancellationToken)))
            .WithName("ExportKnowledgeDocument");

        knowledge.MapGet(
                "/{knowledgeId}/relations",
                async (
                    string knowledgeId,
                    string? status,
                    int? pageSize,
                    string? cursor,
                    KnowledgeService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListRelationsAsync(
                        ApiQueryBinding.ParseGuid(knowledgeId, "knowledgeId"),
                        ParseRelationStatus(status),
                        pageSize ?? 20,
                        cursor,
                        cancellationToken)))
            .WithName("ListKnowledgeRelations");

        endpoints.MapGet(
                "/api/graph/local",
                async (
                    string? centerKnowledgeId,
                    int? depth,
                    int? maxNodes,
                    int? maxEdges,
                    KnowledgeService service,
                    CancellationToken cancellationToken) =>
                {
                    if (centerKnowledgeId is null)
                    {
                        throw ApiQueryBinding.InvalidQuery(
                            "centerKnowledgeId is required.");
                    }
                    return Results.Ok(await service.GetLocalGraphAsync(
                        ApiQueryBinding.ParseGuid(
                            centerKnowledgeId,
                            "centerKnowledgeId"),
                        depth ?? 2,
                        maxNodes ?? 200,
                        maxEdges ?? 400,
                        cancellationToken));
                })
            .RequireAuthorization()
            .WithName("GetLocalKnowledgeGraph");

        endpoints.MapPost(
                "/api/knowledge/retrospective-draft",
                async (
                    CreateKnowledgeRetrospectiveDraftApiRequest request,
                    KnowledgeRetrospectiveService retrospectives,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var idempotencyKey =
                        ApiInputValidation.RequireIdempotencyKey(
                            context.Request.Headers["Idempotency-Key"]
                                .ToString());
                    var result = await retrospectives.CreateAsync(
                        request.ToDomain(),
                        context.CreateUserActor(),
                        idempotencyKey,
                        cancellationToken);
                    var location = $"/api/drafts/{result.DraftId:D}";
                    context.Response.Headers.Location = location;
                    return Results.Created(location, result);
                })
            .RequireAuthorization(
                McpBearerAuthentication.UserUiOrMcpPolicy)
            .AddEndpointFilter<RejectMcpIdentityEndpointFilter>()
            .RequireAntiforgery()
            .WithName("CreateKnowledgeRetrospectiveDraft");

        endpoints.MapPost(
                "/api/knowledge/synthesis-draft",
                async (
                    CreateKnowledgeSynthesisDraftApiRequest request,
                    KnowledgeSynthesisService syntheses,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var idempotencyKey =
                        ApiInputValidation.RequireIdempotencyKey(
                            context.Request.Headers["Idempotency-Key"]
                                .ToString());
                    var result = await syntheses.CreateAsync(
                        request.ToDomain(),
                        context.CreateUserActor(),
                        idempotencyKey,
                        cancellationToken);
                    var location = $"/api/drafts/{result.DraftId:D}";
                    context.Response.Headers.Location = location;
                    return Results.Created(location, result);
                })
            .RequireAuthorization(
                McpBearerAuthentication.UserUiOrMcpPolicy)
            .AddEndpointFilter<RejectMcpIdentityEndpointFilter>()
            .RequireAntiforgery()
            .WithName("CreateKnowledgeSynthesisDraft");
        return endpoints;
    }

    private static KnowledgeStatus? ParseStatus(string? value) =>
        value switch
        {
            null => null,
            "active" => KnowledgeStatus.Active,
            "archived" => KnowledgeStatus.Archived,
            _ => throw Invalid("status")
        };

    private static KnowledgeRelationStatus? ParseRelationStatus(
        string? value) =>
        value switch
        {
            null => null,
            "active" => KnowledgeRelationStatus.Active,
            "retracted" => KnowledgeRelationStatus.Retracted,
            _ => throw Invalid("status")
        };

    private static ApiRequestException Invalid(string field) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            $"{field} is invalid.");
}
