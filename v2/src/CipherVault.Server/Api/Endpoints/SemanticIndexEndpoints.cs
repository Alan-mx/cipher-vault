using CipherVault.Application.Semantic;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;

namespace CipherVault.Server.Api.Endpoints;

public static class SemanticIndexEndpoints
{
    public static IEndpointRouteBuilder MapSemanticIndexEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/semantic-index")
            .RequireAuthorization();

        group.MapGet(
                "/status",
                async (
                    SemanticIndexService semantic,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var status = await semantic.GetStatusAsync(
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(status.ToApiResponse());
                })
            .WithName("GetSemanticIndexStatus");

        group.MapPost(
                "/remote-consent/grant",
                async (
                    SemanticConsentApiRequest request,
                    SemanticIndexService semantic,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var status = await semantic.GrantRemoteConsentAsync(
                        key,
                        SemanticIndexApiMapper.ParseProfileId(
                            request.ProfileId),
                        SemanticIndexApiMapper.ParseEndpointFingerprint(
                            request.EndpointFingerprint),
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(status.ToApiResponse());
                })
            .RequireAntiforgery()
            .WithName("GrantSemanticRemoteConsent");

        group.MapPost(
                "/remote-consent/revoke",
                async (
                    SemanticConsentApiRequest request,
                    SemanticIndexService semantic,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var status = await semantic.RevokeRemoteConsentAsync(
                        key,
                        SemanticIndexApiMapper.ParseProfileId(
                            request.ProfileId),
                        SemanticIndexApiMapper.ParseEndpointFingerprint(
                            request.EndpointFingerprint),
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(status.ToApiResponse());
                })
            .RequireAntiforgery()
            .WithName("RevokeSemanticRemoteConsent");

        group.MapPost(
                "/rebuild",
                async (
                    SemanticRebuildApiRequest request,
                    SemanticIndexService semantic,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var status = await semantic.RebuildAsync(
                        key,
                        SemanticIndexApiMapper.ParseProfileId(
                            request.ProfileId),
                        context.CreateUserActor(),
                        cancellationToken);
                    return Results.Ok(status.ToApiResponse());
                })
            .RequireAntiforgery()
            .WithName("RebuildSemanticIndex");

        return endpoints;
    }
}
