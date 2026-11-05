using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class UnifiedDraftEndpoints
{
    public static IEndpointRouteBuilder MapUnifiedDraftEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/drafts")
            .RequireAuthorization();
        group.MapGet(
                "/summary",
                async (
                    UnifiedDraftService drafts,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await drafts.GetSummaryAsync(cancellationToken)))
            .WithName("GetUnifiedDraftSummary");

        group.MapGet(
                "",
                async (
                    string? status,
                    string? targetObjectType,
                    string? operationType,
                    string? sourceActor,
                    string? targetId,
                    DateTimeOffset? updatedAfter,
                    DateTimeOffset? updatedBefore,
                    int? pageSize,
                    string? cursor,
                    UnifiedDraftService drafts,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await drafts.ListAsync(
                        new DraftQuery(
                            ParseStatus(status),
                            ParseTarget(targetObjectType),
                            ParseOperation(operationType),
                            sourceActor,
                            targetId is null
                                ? null
                                : ApiQueryBinding.ParseGuid(targetId, "targetId"),
                            updatedAfter,
                            updatedBefore,
                            pageSize ?? 50,
                            cursor),
                        cancellationToken)))
            .WithName("ListUnifiedDrafts");

        group.MapGet(
                "/{draftId}",
                async (
                    string draftId,
                    UnifiedDraftService drafts,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await drafts.GetAsync(
                        ApiQueryBinding.ParseGuid(draftId, "draftId"),
                        cancellationToken)))
            .WithName("GetUnifiedDraft");

        group.MapPost(
                "",
                async (
                    CreateUnifiedDraftApiRequest request,
                    UnifiedDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await drafts.CreateAsync(
                        request.ToDomain(),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    context.Response.Headers.Location =
                        $"/api/drafts/{result.DraftId:D}";
                    return Results.Created(
                        context.Response.Headers.Location,
                        result);
                })
            .RequireAntiforgery()
            .WithName("CreateUnifiedDraft");

        endpoints.MapPut(
                "/api/drafts/{draftId}",
                async (
                    string draftId,
                    UpdateUnifiedDraftApiRequest request,
                    UnifiedDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    return Results.Ok(await drafts.UpdateAsync(
                        ApiQueryBinding.ParseGuid(draftId, "draftId"),
                        request.ExpectedDraftRevision,
                        request.Payload,
                        context.CreateUserActor(),
                        key,
                        cancellationToken));
                })
            .RequireAuthorization(
                McpBearerAuthentication.UserUiOrMcpPolicy)
            .AddEndpointFilter<RejectMcpIdentityEndpointFilter>()
            .RequireAntiforgery()
            .WithName("UpdateUnifiedDraft");

        group.MapPost(
                "/{draftId}/rebase",
                async (
                    string draftId,
                    RebaseUnifiedDraftApiRequest request,
                    UnifiedDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    return Results.Ok(await drafts.RebaseAsync(
                        ApiQueryBinding.ParseGuid(draftId, "draftId"),
                        request.ExpectedDraftRevision,
                        request.ObservedTargetRevision,
                        context.CreateUserActor(),
                        key,
                        cancellationToken));
                })
            .RequireAntiforgery()
            .WithName("RebaseUnifiedDraft");

        MapAction(
            group,
            "submit-review",
            "SubmitUnifiedDraft",
            (service, id, revision, actor, key, token) =>
                service.SubmitAsync(id, revision, actor, key, token));
        MapAction(
            group,
            "discard",
            "DiscardUnifiedDraft",
            (service, id, revision, actor, key, token) =>
                service.DiscardAsync(id, revision, actor, key, token));
        MapAction(
            group,
            "reject",
            "RejectUnifiedDraft",
            (service, id, revision, actor, key, token) =>
                service.RejectAsync(id, revision, actor, key, token));

        group.MapPost(
                "/{draftId}/apply",
                async (
                    string draftId,
                    UnifiedDraftRevisionApiRequest request,
                    UnifiedDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    return Results.Ok(await drafts.ApplyAsync(
                        ApiQueryBinding.ParseGuid(draftId, "draftId"),
                        request.ExpectedDraftRevision,
                        context.CreateUserActor(),
                        key,
                        cancellationToken));
                })
            .RequireAntiforgery()
            .WithName("ApplyUnifiedDraft");
        return endpoints;
    }

    private static void MapAction(
        RouteGroupBuilder group,
        string suffix,
        string name,
        Func<
            UnifiedDraftService,
            Guid,
            int,
            CipherVault.Core.Documents.VaultActorContext,
            Guid,
            CancellationToken,
            Task<DraftMutation>> action)
    {
        group.MapPost(
                $"/{{draftId}}/{suffix}",
                async (
                    string draftId,
                    UnifiedDraftRevisionApiRequest request,
                    UnifiedDraftService drafts,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    return Results.Ok(await action(
                        drafts,
                        ApiQueryBinding.ParseGuid(draftId, "draftId"),
                        request.ExpectedDraftRevision,
                        context.CreateUserActor(),
                        key,
                        cancellationToken));
                })
            .RequireAntiforgery()
            .WithName(name);
    }

    private static DraftStatus? ParseStatus(string? value) =>
        value is null
            ? null
            : TryParse(() => KnowledgeBaseValueMapping.ParseDraftStatus(value));

    private static DraftTargetObjectType? ParseTarget(string? value) =>
        value is null
            ? null
            : TryParse(() =>
                KnowledgeBaseValueMapping.ParseTargetObjectType(value));

    private static DraftOperationType? ParseOperation(string? value) =>
        value is null
            ? null
            : TryParse(() =>
                KnowledgeBaseValueMapping.ParseOperationType(value));

    private static T TryParse<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidInput,
                "Draft filter is invalid.");
        }
    }
}
