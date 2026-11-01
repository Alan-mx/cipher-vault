using CipherVault.Application.Exports;
using CipherVault.Core.Exports;
using CipherVault.Server.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace CipherVault.Server.Api.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/exports");
        group.MapPost(
                "/plaintext",
                async Task (
                    HttpRequest request,
                    HttpResponse response,
                    HttpContext context,
                    PlaintextExportService exports,
                    CancellationToken cancellationToken) =>
                {
                    var form = await request.ReadFormAsync(cancellationToken);
                    if (!string.Equals(
                            form["confirmation"].ToString(),
                            PlaintextExportLimits.ConfirmationPhrase,
                            StringComparison.Ordinal))
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.InvalidInput,
                            "Plaintext export confirmation is invalid.");
                    }

                    try
                    {
                        var bodyControl = context.Features
                            .Get<IHttpBodyControlFeature>();
                        if (bodyControl is not null)
                        {
                            bodyControl.AllowSynchronousIO = true;
                        }

                        await exports.WriteAsync(
                            response.Body,
                            metadata =>
                            {
                                response.StatusCode = StatusCodes.Status200OK;
                                response.ContentType = "application/zip";
                                response.Headers.AcceptRanges = "none";
                                response.Headers.CacheControl = "no-store";
                                response.Headers.ContentDisposition =
                                    new ContentDispositionHeaderValue("attachment")
                                    {
                                        FileName = metadata.FileName
                                    }.ToString();
                            },
                            context.CreateUserActor(),
                            cancellationToken);
                    }
                    catch
                    {
                        if (!response.HasStarted)
                        {
                            response.Clear();
                        }
                        throw;
                    }
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("ExportPlaintext");
        return endpoints;
    }
}
