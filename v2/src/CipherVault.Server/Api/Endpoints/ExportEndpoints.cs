using System.Text.Json;
using CipherVault.Application.Exports;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace CipherVault.Server.Api.Endpoints;

public static class ExportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    public static IEndpointRouteBuilder MapExportEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/exports");
        group.MapPost(
                "/plaintext/preflight",
                async Task<IResult> (
                    HttpRequest request,
                    HttpContext context,
                    PlaintextExportService exports,
                    VaultSessionManager sessions,
                    ExportDownloadTicketStore tickets,
                    CancellationToken cancellationToken) =>
                {
                    var confirmation = await ReadConfirmationAsync(
                        request,
                        cancellationToken);
                    if (!string.Equals(
                            confirmation,
                            PlaintextExportLimits.ConfirmationPhrase,
                            StringComparison.Ordinal))
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.InvalidInput,
                            "Plaintext export confirmation is invalid.");
                    }

                    var preflight = await exports.PreflightAsync(
                        cancellationToken);
                    var current = sessions.Session
                        ?? throw new VaultException(
                            VaultErrorCode.VaultLocked,
                            "Vault is locked.");
                    var issued = tickets.Issue(
                        current.SessionId,
                        current.Generation,
                        context.CreateUserActor());
                    if (issued is null)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status503ServiceUnavailable,
                            "export_ticket_capacity_exceeded",
                            "Plaintext export ticket capacity is exhausted.");
                    }

                    return Results.Ok(
                        new PlaintextExportPreflightResponse(
                            issued.Ticket,
                            issued.ExpiresAtUtc,
                            preflight.DocumentCount,
                            preflight.TotalContentBytes));
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("PreflightPlaintextExport");

        group.MapPost(
                "/plaintext/download",
                async Task (
                    HttpRequest request,
                    HttpResponse response,
                    HttpContext context,
                    PlaintextExportService exports,
                    VaultSessionManager sessions,
                    ExportDownloadTicketStore tickets,
                    CancellationToken cancellationToken) =>
                {
                    var form = await request.ReadFormAsync(cancellationToken);
                    var current = sessions.Session
                        ?? throw new VaultException(
                            VaultErrorCode.VaultLocked,
                            "Vault is locked.");
                    var consumed = tickets.TryConsume(
                        form["ticket"].ToString(),
                        current.SessionId,
                        current.Generation,
                        out var binding);
                    if (consumed == ExportTicketConsumeResult.Invalid)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status404NotFound,
                            "export_ticket_invalid",
                            "Plaintext export ticket is invalid.");
                    }
                    if (consumed == ExportTicketConsumeResult.SessionMismatch)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status403Forbidden,
                            "export_ticket_session_mismatch",
                            "Plaintext export ticket belongs to another Session.");
                    }

                    var actor = new VaultActorContext(
                        binding.ActorKind,
                        binding.CorrelationId);
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
                            actor,
                            cancellationToken,
                            binding.SessionId,
                            binding.SessionGeneration);
                    }
                    catch (VaultException exception)
                        when (exception.Code == VaultErrorCode.Forbidden
                            && !response.HasStarted)
                    {
                        await ApiResponseWriter.WriteErrorAsync(
                            context,
                            StatusCodes.Status403Forbidden,
                            "export_ticket_session_mismatch",
                            "Plaintext export ticket Session is no longer active.");
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
            .WithName("DownloadPlaintextExport");
        return endpoints;
    }

    private static async Task<string> ReadConfirmationAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasJsonContentType())
        {
            var payload = await JsonSerializer.DeserializeAsync<
                PlaintextExportPreflightRequest>(
                    request.Body,
                    JsonOptions,
                    cancellationToken: cancellationToken);
            return payload?.Confirmation ?? string.Empty;
        }

        var form = await request.ReadFormAsync(cancellationToken);
        return form["confirmation"].ToString();
    }
}

internal sealed record PlaintextExportPreflightRequest(string Confirmation);

internal sealed record PlaintextExportPreflightResponse(
    string Ticket,
    DateTimeOffset ExpiresAtUtc,
    int DocumentCount,
    long TotalContentBytes);
