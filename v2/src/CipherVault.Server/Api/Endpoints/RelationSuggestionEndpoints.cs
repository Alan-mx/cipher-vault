using CipherVault.Application.Relations;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Relations;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class RelationSuggestionEndpoints
{
    public static IEndpointRouteBuilder MapRelationSuggestionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var suggestions = endpoints
            .MapGroup("/api/relation-suggestions")
            .RequireAuthorization();

        suggestions.MapGet(
                "/summary",
                async (
                    RelationSuggestionService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var summary = await service.GetSummaryAsync(
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(summary.ToApiResponse());
                })
            .WithName("GetRelationSuggestionSummary");

        suggestions.MapGet(
                "",
                async (
                    string? status,
                    string? relationType,
                    string? sourceDocumentId,
                    string? targetDocumentId,
                    string? updatedAfter,
                    string? updatedBefore,
                    int? pageSize,
                    string? cursor,
                    RelationSuggestionService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var query = new RelationSuggestionQuery(
                        RelationSuggestionApiMapper.ParseStatus(status),
                        RelationSuggestionApiMapper.ParseRelationType(
                            relationType),
                        sourceDocumentId is null
                            ? null
                            : RelationSuggestionApiMapper.ParseGuid(
                                sourceDocumentId,
                                "sourceDocumentId"),
                        targetDocumentId is null
                            ? null
                            : RelationSuggestionApiMapper.ParseGuid(
                                targetDocumentId,
                                "targetDocumentId"),
                        RelationSuggestionApiMapper.ParseTimestamp(
                            updatedAfter,
                            "updatedAfter"),
                        RelationSuggestionApiMapper.ParseTimestamp(
                            updatedBefore,
                            "updatedBefore"),
                        new CursorPageRequest(
                            RelationSuggestionApiMapper.ParsePageSize(
                                pageSize),
                            cursor));
                    var page = await service.ListAsync(
                        query,
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(page.ToApiResponse());
                })
            .WithName("ListRelationSuggestions");

        suggestions.MapGet(
                "/{id}",
                async (
                    string id,
                    RelationSuggestionService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var detail = await service.GetAsync(
                        RelationSuggestionApiMapper.ParseGuid(id, "id"),
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(detail);
                })
            .WithName("GetRelationSuggestion");

        suggestions.MapPost(
                "/{id}/apply",
                async (
                    string id,
                    RelationSuggestionRevisionApiRequest request,
                    RelationReviewService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await service.ApplyAsync(
                        RelationSuggestionApiMapper.ParseGuid(id, "id"),
                        RelationSuggestionApiMapper.ParseExpectedRevision(
                            request.ExpectedSuggestionRevision),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    if (result.Status == RelationSuggestionStatus.Stale)
                    {
                        throw new VaultException(
                            VaultErrorCode.RelationSuggestionStale,
                            "Relation suggestion source or target changed.",
                            details: new Dictionary<string, object?>
                            {
                                ["currentSuggestionRevision"] =
                                    result.SuggestionRevision,
                                ["currentSourceRevision"] =
                                    result.CurrentSourceRevision,
                                ["currentTargetRevision"] =
                                    result.CurrentTargetRevision,
                                ["status"] = "stale"
                            });
                    }
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAntiforgery()
            .WithName("ApplyRelationSuggestion");

        suggestions.MapPost(
                "/{id}/reject",
                async (
                    string id,
                    RelationSuggestionRevisionApiRequest request,
                    RelationReviewService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await service.RejectAsync(
                        RelationSuggestionApiMapper.ParseGuid(id, "id"),
                        RelationSuggestionApiMapper.ParseExpectedRevision(
                            request.ExpectedSuggestionRevision),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAntiforgery()
            .WithName("RejectRelationSuggestion");

        endpoints.MapGet(
                "/api/documents/{id}/relations",
                async (
                    string id,
                    RelationReviewService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var relations = await service.ListDocumentRelationsAsync(
                        RelationSuggestionApiMapper.ParseGuid(id, "id"),
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(relations);
                })
            .RequireAuthorization()
            .WithName("ListDocumentRelations");

        endpoints.MapPost(
                "/api/document-relations/{id}/retract",
                async (
                    string id,
                    RelationReviewService service,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await service.RetractAsync(
                    RelationSuggestionApiMapper.ParseGuid(id, "id"),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("RetractDocumentRelation");
        return endpoints;
    }
}
