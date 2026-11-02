using CipherVault.Application.Backup;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Application.Vault;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Backup;
using CipherVault.Server.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace CipherVault.Server.Api.Endpoints;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/backups");
        group.MapGet(
                "",
                async (
                    int? pageSize,
                    string? cursor,
                    BackupService backups,
                    CancellationToken cancellationToken) =>
                {
                    var page = await backups.ListAsync(
                        new BackupQuery(
                            new CursorPageRequest(pageSize, cursor)),
                        cancellationToken);
                    return Results.Ok(new BackupListApiResponse(
                        page.Items
                            .Select(BackupListApiItem.FromDomain)
                            .ToArray(),
                        page.NextCursor));
                })
            .RequireAuthorization()
            .WithName("ListBackups");

        group.MapGet(
                "/git/status",
                async (
                    HttpContext context,
                    VaultSessionManager sessions,
                    GitBackupCoordinator gitBackup,
                    CancellationToken cancellationToken) =>
                {
                    var vaultStatus = await sessions.GetStatusAsync(
                        cancellationToken);
                    if (vaultStatus.State != VaultSessionState.Unlocked)
                    {
                        throw new VaultException(
                            VaultErrorCode.VaultLocked,
                            "Vault is locked.");
                    }

                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(
                        GitBackupStatusApiResponse.FromDomain(
                            await gitBackup.GetStatusAsync(
                                cancellationToken)));
                })
            .RequireAuthorization()
            .WithName("GetGitBackupStatus");

        group.MapPost(
                "",
                async (
                    HttpRequest request,
                    HttpContext context,
                    BackupService backups,
                    ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    var bodyFeature = context.Features
                        .Get<IHttpRequestBodyDetectionFeature>();
                    if (bodyFeature?.CanHaveBody == true
                        || request.ContentLength is > 0
                        || request.Headers.TransferEncoding.Count > 0)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.InvalidInput,
                            "Backup creation does not accept a request body.");
                    }

                    var result = await backups.CreateAsync(
                        context.CreateUserActor(),
                        cancellationToken);
                    if (!result.AuditRecorded)
                    {
                        loggerFactory
                            .CreateLogger("CipherVault.Server.Backup")
                            .LogWarning(
                                new EventId(3200, "BackupAuditFailed"),
                                "Backup was published but audit recording failed.");
                    }

                    var item = BackupListApiItem.FromDomain(result.Item);
                    context.Response.Headers.Location =
                        $"/api/backups/{Uri.EscapeDataString(item.FileName)}/download";
                    return Results.Created(
                        context.Response.Headers.Location,
                        new BackupCreateApiResponse(item));
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("CreateBackup");

        group.MapGet(
                "/{fileName}/download",
                async Task (
                    string fileName,
                    HttpContext context,
                    BackupService backups,
                    ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    var actor = context.CreateUserActor();
                    await using var lease =
                        await backups.BeginDownloadAsync(
                            fileName,
                            actor,
                            cancellationToken);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength = lease.Bytes;
                    context.Response.Headers.AcceptRanges = "none";
                    context.Response.Headers.ContentDisposition =
                        new ContentDispositionHeaderValue("attachment")
                        {
                            FileName = lease.FileName
                        }.ToString();
                    var outcome = "failed";
                    string? errorCode = "download_failed";
                    long bytesWritten = 0;
                    try
                    {
                        var buffer = new byte[64 * 1024];
                        while (true)
                        {
                            var read = await lease.Content.ReadAsync(
                                buffer,
                                cancellationToken);
                            if (read == 0)
                            {
                                break;
                            }

                            await context.Response.Body.WriteAsync(
                                buffer.AsMemory(0, read),
                                cancellationToken);
                            bytesWritten += read;
                        }

                        await context.Response.Body.FlushAsync(
                            cancellationToken);
                        var after = RegularFileIdentity.Read(
                            lease.Identity.CanonicalPath);
                        if (bytesWritten != lease.Bytes
                            || after != lease.Identity)
                        {
                            throw new VaultException(
                                VaultErrorCode.StorageError,
                                "Backup file changed during download.");
                        }

                        outcome = "success";
                        errorCode = null;
                    }
                    catch (OperationCanceledException)
                    {
                        outcome = "cancelled";
                        errorCode = "download_cancelled";
                        throw;
                    }
                    catch (VaultException)
                    {
                        outcome = "failed";
                        errorCode = "download_failed";
                        throw;
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or System.Security.SecurityException)
                    {
                        outcome = "failed";
                        errorCode = "download_failed";
                        throw;
                    }
                    finally
                    {
                        if (!await backups.RecordDownloadTerminalAsync(
                                lease,
                                actor,
                                outcome,
                                bytesWritten,
                                errorCode))
                        {
                            loggerFactory
                                .CreateLogger("CipherVault.Server.Backup")
                                .LogWarning(
                                    new EventId(
                                        3201,
                                        "BackupDownloadAuditFailed"),
                                    "Backup download terminal audit recording failed.");
                        }
                    }
                })
            .RequireAuthorization()
            .WithName("DownloadBackup");
        return endpoints;
    }
}
