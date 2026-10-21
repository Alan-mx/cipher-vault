using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Http;
using CipherVault.Host.Security;
using CipherVault.Host.Session;

namespace CipherVault.Host;

public static class HostApplication
{
    public const string ProtocolVersion = "1";

    public static WebApplication Configure(
        WebApplicationBuilder builder,
        ResolvedCipherVaultHostConfig configuration,
        IAdminTokenStore adminTokenStore,
        IHostCredentialPrompt credentialPrompt,
        ISystemActivityProvider? activityProvider = null)
    {
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(adminTokenStore);
        builder.Services.AddSingleton(credentialPrompt);
        builder.Services.AddSingleton<AgentTokenStore>();
        builder.Services.AddSingleton<IVaultService>(_ => new VaultService(
            activityProvider ?? new WindowsSystemActivityProvider(),
            sessionOptions: configuration.SessionOptions));
        builder.Services.AddSingleton<HostSessionCoordinator>();
        builder.Services.AddSingleton<HostVaultGateway>();

        var app = builder.Build();
        app.UseMiddleware<HostRequestValidationMiddleware>();
        app.UseMiddleware<AdminTokenAuthenticationMiddleware>();
        app.MapGet(
            configuration.Endpoint.AdminPath + "/health",
            () => Results.Json(
                new HostApiResponse<HostHealthData>(
                    true,
                    new HostHealthData(
                        "ready",
                        typeof(HostApplication).Assembly.GetName().Version?.ToString()
                            ?? "1.0.0",
                        ProtocolVersion))));
        app.MapGet(
            configuration.Endpoint.AdminPath + "/status",
            async (HostSessionCoordinator sessions, CancellationToken cancellationToken) =>
            {
                var snapshot = await sessions.GetSnapshotAsync(cancellationToken);
                return Results.Json(
                    new HostApiResponse<HostStatusData>(
                        true,
                        new HostStatusData(
                            "ready",
                            typeof(HostApplication).Assembly.GetName().Version?.ToString()
                                ?? "1.0.0",
                            ProtocolVersion,
                            new HostEndpointStatusData(
                                $"http://{configuration.Endpoint.BindAddress}:{configuration.Endpoint.Port}",
                                configuration.Endpoint.McpPath,
                                configuration.Endpoint.AdminPath),
                            HostDtoMapper.ToSessionStatus(snapshot))));
            });
        app.MapGet(
            configuration.Endpoint.AdminPath + "/clients",
            async (AgentTokenStore clients, CancellationToken cancellationToken) =>
            {
                var items = await clients.ListAsync(cancellationToken);
                return Results.Json(new HostApiResponse<IReadOnlyList<HostClientData>>(
                    true,
                    items));
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/clients",
            async (
                HostCreateClientRequest request,
                AgentTokenStore clients,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var created = await clients.CreateAsync(request, cancellationToken);
                    return Results.Json(
                        new HostApiResponse<HostCreateClientData>(true, created),
                        statusCode: StatusCodes.Status201Created);
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPatch(
            configuration.Endpoint.AdminPath + "/clients/{clientId}",
            async (
                string clientId,
                HostUpdateClientRequest request,
                AgentTokenStore clients,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var updated = await clients.UpdateAsync(
                        clientId,
                        request,
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<HostClientData>(true, updated));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/clients/{clientId}/rotate",
            async (
                string clientId,
                AgentTokenStore clients,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var rotated = await clients.RotateAsync(
                        clientId,
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<HostCreateClientData>(
                            true,
                            rotated));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapDelete(
            configuration.Endpoint.AdminPath + "/clients/{clientId}",
            async (
                string clientId,
                AgentTokenStore clients,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await clients.DeleteAsync(clientId, cancellationToken);
                    return Results.NoContent();
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapGet(
            configuration.Endpoint.AdminPath + "/catalog",
            async (
                string? folder,
                string? cursor,
                int? limit,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var page = await vault.ExecuteAsync(
                        (service, token) => service.ListCatalogAsync(
                            folder,
                            cursor,
                            limit ?? 50,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultCatalogPage>(true, page));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapGet(
            configuration.Endpoint.AdminPath + "/search",
            async (
                string q,
                string? folder,
                string? cursor,
                int? limit,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var page = await vault.ExecuteAsync(
                        (service, token) => service.SearchNotesAsync(
                            q,
                            folder,
                            cursor,
                            limit ?? 20,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultSearchPage>(true, page));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapGet(
            configuration.Endpoint.AdminPath + "/notes/{documentId:guid}",
            async (
                Guid documentId,
                int? offset,
                int? maxChars,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.ReadNoteAsync(
                            documentId,
                            offset ?? 0,
                            maxChars ?? 16_000,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultReadResult>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/notes",
            async (
                HostCreateNoteRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.CreateNoteAsync(
                            request.Title ?? string.Empty,
                            request.Folder,
                            request.Format ?? "md",
                            request.Content ?? string.Empty,
                            request.Tags,
                            VaultActorKind.UserUi,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultWriteResult>(true, result),
                        statusCode: StatusCodes.Status201Created);
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/notes/{documentId:guid}/append",
            async (
                Guid documentId,
                HostAppendNoteRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.AppendNoteAsync(
                            documentId,
                            request.ExpectedRevision,
                            request.Content ?? string.Empty,
                            VaultActorKind.UserUi,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultWriteResult>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPatch(
            configuration.Endpoint.AdminPath + "/notes/{documentId:guid}",
            async (
                Guid documentId,
                HostUpdateNoteRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.UpdateNoteAsync(
                            documentId,
                            request.ExpectedRevision,
                            new VaultNotePatch(
                                request.Title,
                                request.Folder,
                                request.Content,
                                request.Tags),
                            VaultActorKind.UserUi,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultWriteResult>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/notes/{documentId:guid}/trash",
            async (
                Guid documentId,
                HostTrashNoteRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.DeleteNoteAsync(
                            documentId,
                            request.ExpectedRevision,
                            VaultActorKind.UserUi,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultDeleteResult>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapGet(
            configuration.Endpoint.AdminPath + "/trash",
            async (
                string? cursor,
                int? limit,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var page = await vault.ExecuteAsync(
                        (service, token) => service.ListTrashAsync(
                            cursor,
                            limit ?? 50,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultTrashPage>(true, page));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/trash/{documentId:guid}/restore",
            async (
                Guid documentId,
                HostRestoreTrashRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.RestoreTrashAsync(
                            documentId,
                            request.ExpectedRevision,
                            request.RenamePath,
                            VaultActorKind.UserUi,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultRestoreResult>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapDelete(
            configuration.Endpoint.AdminPath + "/trash/{documentId:guid}",
            async (
                Guid documentId,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.PurgeNoteAsync(
                            documentId,
                            VaultActorKind.UserUi,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultPurgeResult>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapGet(
            configuration.Endpoint.AdminPath + "/notes/{documentId:guid}/revisions",
            async (
                Guid documentId,
                string? cursor,
                int? limit,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var page = await vault.ExecuteAsync(
                        (service, token) => service.ListRevisionsAsync(
                            documentId,
                            cursor,
                            limit ?? 50,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultRevisionPage>(true, page));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath +
                "/notes/{documentId:guid}/revisions/{revisionId:guid}/restore",
            async (
                Guid documentId,
                Guid revisionId,
                HostRestoreRevisionRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteAsync(
                        (service, token) => service.RestoreRevisionAsync(
                            documentId,
                            revisionId,
                            request.ExpectedRevision,
                            VaultActorKind.UserUi,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultWriteResult>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception),
                        HostHttpResponses.ToHostErrorCode(exception),
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/session/unlock",
            async (
                HostUnlockRequest request,
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseCredentialPreference(
                        request.CredentialType,
                        out var preference))
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        HostErrorCode.InvalidInput,
                        "credentialType must be any, password or recoveryKey.");
                    return Results.Empty;
                }

                try
                {
                    var snapshot = await sessions.UnlockAsync(
                        preference,
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<HostSessionStatusData>(
                            true,
                            HostDtoMapper.ToSessionStatus(snapshot)));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        HostErrorCode.InvalidInput,
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/vault/initialize",
            async (
                HostInitializeRequest request,
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.VaultPath))
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        HostErrorCode.InvalidInput,
                        "vaultPath is required.");
                    return Results.Empty;
                }

                try
                {
                    var result = await sessions.InitializeAsync(
                        request.VaultPath,
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<HostInitializeData>(true, result));
                }
                catch (HostException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        HostHttpResponses.ToStatusCode(exception.Code),
                        exception.Code,
                        exception.Message);
                    return Results.Empty;
                }
                catch (VaultException exception)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        HostErrorCode.InvalidInput,
                        exception.Message);
                    return Results.Empty;
                }
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/session/lock",
            async (
                HostLockRequest request,
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var reason = string.IsNullOrWhiteSpace(request.Reason)
                    ? "user-requested"
                    : request.Reason.Trim();
                if (reason.Length > 200)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        HostErrorCode.InvalidInput,
                        "Lock reason is too long.");
                    return Results.Empty;
                }

                var snapshot = await sessions.LockAsync(reason, cancellationToken);
                if (snapshot.State != VaultSessionState.Locked)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status409Conflict,
                        HostErrorCode.LockInProgress,
                        "Vault lock is still in progress.");
                    return Results.Empty;
                }

                return Results.Json(
                    new HostApiResponse<HostSessionStatusData>(
                        true,
                        HostDtoMapper.ToSessionStatus(snapshot)));
            });
        app.MapPost(
            configuration.Endpoint.AdminPath + "/host/stop",
            async (
                HostStopRequest request,
                HostSessionCoordinator sessions,
                IHostApplicationLifetime lifetime,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var reason = string.IsNullOrWhiteSpace(request.Reason)
                    ? "user-requested"
                    : request.Reason.Trim();
                if (reason.Length > 200)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        HostErrorCode.InvalidInput,
                        "Stop reason is too long.");
                    return Results.Empty;
                }

                var snapshot = await sessions.LockAsync(reason, cancellationToken);
                if (snapshot.State != VaultSessionState.Locked)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status409Conflict,
                        HostErrorCode.LockInProgress,
                        "Vault lock is still in progress.");
                    return Results.Empty;
                }

                context.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
                return Results.NoContent();
            });
        return app;
    }

    private static bool TryParseCredentialPreference(
        string? value,
        out HostCredentialPreference preference)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "any":
                preference = HostCredentialPreference.Any;
                return true;
            case "password":
                preference = HostCredentialPreference.Password;
                return true;
            case "recoverykey":
            case "recovery-key":
                preference = HostCredentialPreference.RecoveryKey;
                return true;
            default:
                preference = HostCredentialPreference.Any;
                return false;
        }
    }
}
