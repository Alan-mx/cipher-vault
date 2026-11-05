using CipherVault.Application.KnowledgeBase;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class KnowledgeModelEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeModelEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/knowledge-model",
                async (
                    KnowledgeModelService models,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var model = await models.GetAsync(cancellationToken);
                    context.Response.SetRevisionETag(model.Version);
                    return Results.Ok(model);
                })
            .RequireAuthorization()
            .WithName("GetKnowledgeModel");

        endpoints.MapPut(
                "/api/knowledge-model",
                async (
                    KnowledgeModelUpdateApiRequest request,
                    KnowledgeModelService models,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var expectedVersion = ParseExpectedVersion(context);
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var model = await models.ReplaceAsync(
                        expectedVersion,
                        request.ToDomain(),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    context.Response.SetRevisionETag(model.Version);
                    return Results.Ok(model);
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("ReplaceKnowledgeModel");
        return endpoints;
    }

    private static int ParseExpectedVersion(HttpContext context)
    {
        var values = context.Request.Headers.IfMatch;
        if (values.Count == 0)
        {
            throw new ApiRequestException(
                StatusCodes.Status428PreconditionRequired,
                ApiErrorCodes.IfMatchRequired,
                "If-Match is required.");
        }
        if (values.Count != 1
            || !RevisionETag.TryParse(values[0], out var etag)
            || etag is null)
        {
            throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidIfMatch,
                "If-Match must contain one positive version ETag.");
        }
        return etag.Revision;
    }
}
