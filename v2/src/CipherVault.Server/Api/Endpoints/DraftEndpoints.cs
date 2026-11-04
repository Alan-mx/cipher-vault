using CipherVault.Application.Drafts;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class DraftEndpoints
{
    public static IEndpointRouteBuilder MapDraftEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/drafts");
        group.MapGet(
                "/summary",
                async (
                    DocumentDraftService drafts,
                    CancellationToken cancellationToken) =>
                {
                    var summary =
                        await drafts.GetSummaryAsync(cancellationToken);
                    return Results.Ok(summary.ToApiResponse());
                })
            .RequireAuthorization()
            .WithName("GetDocumentDraftSummary");

        group.MapGet(
                "",
                async (
                    string? queue,
                    string? status,
                    string? type,
                    string? sourceActor,
                    string? targetDocumentId,
                    string? updatedAfter,
                    string? updatedBefore,
                    string? pageSize,
                    string? cursor,
                    DocumentDraftService drafts,
                    CancellationToken cancellationToken) =>
                {
                    var query = new DocumentDraftQuery(
                        DocumentDraftApiMapper.ParseQueue(queue),
                        DocumentDraftApiMapper.ParseStatus(status),
                        DocumentDraftApiMapper.ParseType(type),
                        DocumentDraftApiMapper.ParseDraftSourceActor(sourceActor),
                        DocumentDraftApiMapper.ParseTimestamp(
                            updatedAfter,
                            "updatedAfter"),
                        DocumentDraftApiMapper.ParseTimestamp(
                            updatedBefore,
                            "updatedBefore"),
                        targetDocumentId is null
                            ? null
                            : ApiQueryBinding.ParseGuid(
                                targetDocumentId,
                                "targetDocumentId"),
                        new CursorPageRequest(
                            DocumentDraftApiMapper.ParsePageSize(pageSize),
                            cursor));
                    var page = await drafts.ListAsync(query, cancellationToken);
                    return Results.Ok(page.ToApiResponse());
                })
            .RequireAuthorization()
            .WithName("ListDocumentDrafts");

        group.MapPost(
                "",
                async (
                    CreateDocumentDraftApiRequest request,
                    DocumentDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await drafts.CreateAsync(
                        request.ToApplicationRequest(),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("CreateDocumentDraft");

        group.MapGet(
                "/{id}",
                async (
                    string id,
                    DocumentDraftService drafts,
                    CancellationToken cancellationToken) =>
                {
                    var draftId = ApiQueryBinding.ParseGuid(id, "id");
                    var detail = await drafts.GetAsync(draftId, cancellationToken);
                    return Results.Ok(detail.ToApiResponse());
                })
            .RequireAuthorization()
            .WithName("GetDocumentDraft");

        group.MapPut(
                "/{id}",
                async (
                    string id,
                    UpdateDocumentDraftApiRequest request,
                    DocumentDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var draftId = ApiQueryBinding.ParseGuid(id, "id");
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await drafts.UpdateAsync(
                        draftId,
                        request.ExpectedDraftRevision,
                        request.Payload,
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("UpdateDocumentDraft");

        group.MapPost(
                "/{id}/rebase",
                async (
                    string id,
                    RebaseDocumentDraftApiRequest request,
                    DocumentDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await drafts.RebaseAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        request.ExpectedDraftRevision,
                        request.ObservedDocumentRevision,
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("RebaseDocumentDraft");

        MapRevisionAction(
            group,
            "submit-review",
            "SubmitDocumentDraftReview",
            (drafts, id, revision, actor, key, token) =>
                drafts.SubmitReviewAsync(id, revision, actor, key, token));
        MapRevisionAction(
            group,
            "reject",
            "RejectDocumentDraft",
            (drafts, id, revision, actor, key, token) =>
                drafts.RejectAsync(id, revision, actor, key, token));
        MapRevisionAction(
            group,
            "discard",
            "DiscardDocumentDraft",
            (drafts, id, revision, actor, key, token) =>
                drafts.DiscardAsync(id, revision, actor, key, token));

        group.MapPost(
                "/{id}/apply",
                async (
                    string id,
                    DocumentDraftRevisionApiRequest request,
                    DocumentDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await drafts.ApplyAsync(
                        ApiQueryBinding.ParseGuid(id, "id"),
                        request.ExpectedDraftRevision,
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("ApplyDocumentDraft");
        return endpoints;
    }

    private static void MapRevisionAction(
        RouteGroupBuilder group,
        string suffix,
        string name,
        Func<
            DocumentDraftService,
            Guid,
            int,
            VaultActorContext,
            Guid,
            CancellationToken,
            Task<DocumentDraftMutationResult>> action)
    {
        group.MapPost(
                $"/{{id}}/{suffix}",
                async (
                    string id,
                    DocumentDraftRevisionApiRequest request,
                    DocumentDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await action(
                        drafts,
                        ApiQueryBinding.ParseGuid(id, "id"),
                        request.ExpectedDraftRevision,
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName(name);
    }
}
