using System.Security.Claims;
using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Client.Security;
using CipherVault.Host.Http;
using CipherVault.Host.Mcp;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using CipherVault.Security.Win;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace CipherVault.Host;

public static class HostApplication
{
    public const string ProtocolVersion = "1";

    public static WebApplication Configure(
        WebApplicationBuilder builder,
        ResolvedCipherVaultHostConfig configuration,
        IAdminTokenStore adminTokenStore,
        IHostCredentialPrompt credentialPrompt,
        IHostAccessGrantPrompt? accessGrantPrompt = null,
        ISystemActivityProvider? activityProvider = null,
        ISecurityEventLog? securityEventLog = null,
        HostAuthenticationRateLimiters? authenticationRateLimiters = null)
    {
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(adminTokenStore);
        builder.Services.AddSingleton(credentialPrompt);
        builder.Services.AddSingleton(
            accessGrantPrompt ?? new WindowsHostAccessGrantPrompt());
        builder.Services.AddSingleton<AgentTokenStore>();
        builder.Services.AddSingleton(serviceProvider => new AccessGrantService(
            securityLog: serviceProvider.GetRequiredService<ISecurityEventLog>()));
        builder.Services.AddSingleton<HostMcpAuthorizationService>();
        builder.Services.AddSingleton(
            authenticationRateLimiters ?? new HostAuthenticationRateLimiters());
        builder.Services.AddSingleton<ISecurityEventLog>(
            securityEventLog ?? new JsonlSecurityEventLog());
        builder.Services.AddSingleton<IVaultService>(_ => new VaultService(
            activityProvider ?? new WindowsSystemActivityProvider(),
            sessionOptions: configuration.SessionOptions));
        builder.Services.AddSingleton<ApprovalStore>();
        builder.Services.AddSingleton<AdminSessionStore>();
        builder.Services.AddSingleton<HostSessionCoordinator>();
        builder.Services.AddSingleton<HostVaultGateway>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<HostVaultTools>();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithTools<HostVaultTools>();

        var app = builder.Build();
        var hostInstanceId = Guid.NewGuid().ToString("D");
        var configFingerprint = HostConfigurationFingerprint.Compute(
            configuration.VaultPath,
            configuration.ConfigPath,
            configuration.Endpoint.BindAddress,
            configuration.Endpoint.Port,
            configuration.Endpoint.McpPath,
            configuration.Endpoint.AdminPath);
        app.UseMiddleware<HostRequestValidationMiddleware>();
        app.UseMiddleware<AgentTokenAuthenticationMiddleware>();
        app.UseMiddleware<AdminTokenAuthenticationMiddleware>();
        app.MapMcp(configuration.Endpoint.McpPath);
        app.MapPost(
            configuration.Endpoint.McpPath + "/access-grants",
            async (
                HostRequestAccessGrantRequest request,
                AccessGrantService grants,
                IHostAccessGrantPrompt accessGrantPrompt,
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var clientId = context.User.FindFirstValue(
                        AgentTokenAuthenticationMiddleware.ClientIdClaim);
                    var accessModeClaim = context.User.FindFirstValue(
                        AgentTokenAuthenticationMiddleware.AccessModeClaim);
                    var authorizationMode = context.User.FindFirstValue(
                        AgentTokenAuthenticationMiddleware.AuthorizationModeClaim);
                    if (string.IsNullOrWhiteSpace(clientId) ||
                        string.IsNullOrWhiteSpace(accessModeClaim) ||
                        !string.Equals(
                            authorizationMode,
                            "accessGrant",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await HostHttpResponses.WriteErrorAsync(
                            context,
                            StatusCodes.Status403Forbidden,
                            HostErrorCode.AccessDenied,
                            "This client does not use accessGrant authorization.");
                        return Results.Empty;
                    }
                    if (request.ClientInstanceId == Guid.Empty)
                    {
                        await HostHttpResponses.WriteErrorAsync(
                            context,
                            StatusCodes.Status400BadRequest,
                            HostErrorCode.InvalidInput,
                            "clientInstanceId is required.");
                        return Results.Empty;
                    }

                    var requestedScopes = ParseAccessGrantScopes(request.Scopes);
                    var snapshot = await sessions.GetSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (snapshot.State is not (
                            VaultSessionState.Unlocked or
                            VaultSessionState.Expiring) ||
                        snapshot.SessionId is null ||
                        snapshot.HardExpiresAt is null)
                    {
                        await HostHttpResponses.WriteErrorAsync(
                            context,
                            StatusCodes.Status423Locked,
                            HostErrorCode.SessionLocked,
                            "Vault must be unlocked before requesting an Access Grant.");
                        return Results.Empty;
                    }

                    var accessMode = string.Equals(
                            accessModeClaim,
                            "readWrite",
                            StringComparison.OrdinalIgnoreCase)
                        ? McpAccessMode.ReadWrite
                        : McpAccessMode.ReadOnly;
                    var generation = new AccessGrantVaultGeneration(
                        snapshot.SessionId.Value,
                        snapshot.HardExpiresAt.Value);
                    var outcome = await grants.RequestAsync(
                        clientId,
                        request.ClientInstanceId,
                        accessMode,
                        requestedScopes,
                        generation,
                        async (approval, token) =>
                        {
                            var prompt = await accessGrantPrompt.RequestAsync(
                                new HostAccessGrantPromptRequest(
                                    approval.ClientId,
                                    approval.ClientInstanceId,
                                    approval.TokenAccessMode,
                                    approval.RequestedScopes,
                                    approval.AllowedScopes,
                                    grants.IdleLifetime,
                                    grants.HardLifetime),
                                token).ConfigureAwait(false);
                            if (!prompt.Approved)
                            {
                                return new AccessGrantApprovalResult(
                                    false,
                                    AccessGrantScope.None,
                                    "user-denied");
                            }

                            var current = await sessions.GetSnapshotAsync(token)
                                .ConfigureAwait(false);
                            if (current.SessionId != generation.SessionId ||
                                current.State is not (
                                    VaultSessionState.Unlocked or
                                    VaultSessionState.Expiring))
                            {
                                return new AccessGrantApprovalResult(
                                    false,
                                    AccessGrantScope.None,
                                    "vault-session-expired");
                            }
                            return new AccessGrantApprovalResult(
                                true,
                                prompt.ApprovedScopes);
                        },
                        cancellationToken).ConfigureAwait(false);

                    switch (outcome.Status)
                    {
                        case AccessGrantRequestStatus.Approved:
                            return Results.Json(
                                new HostApiResponse<HostIssuedAccessGrantData>(
                                    true,
                                    new HostIssuedAccessGrantData(
                                        HostDtoMapper.ToAccessGrantData(
                                            outcome.IssuedGrant!.Record),
                                        outcome.IssuedGrant.PlaintextSecret)),
                                statusCode: StatusCodes.Status201Created);
                        case AccessGrantRequestStatus.Cooldown:
                            if (outcome.RetryAfter is not null)
                            {
                                context.Response.Headers.RetryAfter = Math.Max(
                                    1,
                                    (int)Math.Ceiling(
                                        (outcome.RetryAfter.Value -
                                         DateTimeOffset.UtcNow).TotalSeconds))
                                    .ToString(
                                        System.Globalization.CultureInfo.InvariantCulture);
                            }
                            await HostHttpResponses.WriteErrorAsync(
                                context,
                                StatusCodes.Status429TooManyRequests,
                                HostErrorCode.AccessGrantCooldown,
                                "Access Grant requests are temporarily blocked.");
                            return Results.Empty;
                        case AccessGrantRequestStatus.Conflict:
                            await HostHttpResponses.WriteErrorAsync(
                                context,
                                StatusCodes.Status409Conflict,
                                HostErrorCode.Conflict,
                                "An Access Grant is already active for this Bridge instance.");
                            return Results.Empty;
                        default:
                            var sessionExpired = string.Equals(
                                outcome.ReasonCode,
                                "vault-session-expired",
                                StringComparison.Ordinal);
                            await HostHttpResponses.WriteErrorAsync(
                                context,
                                sessionExpired
                                    ? StatusCodes.Status423Locked
                                    : StatusCodes.Status403Forbidden,
                                sessionExpired
                                    ? HostErrorCode.SessionLocked
                                    : HostErrorCode.AccessDenied,
                                sessionExpired
                                    ? "Vault session changed before the Access Grant was approved."
                                    : "Access Grant request was denied.");
                            return Results.Empty;
                    }
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
            configuration.Endpoint.McpPath + "/access-grants/{grantId:guid}",
            async (
                Guid grantId,
                AccessGrantService grants,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var clientId = context.User.FindFirstValue(
                    AgentTokenAuthenticationMiddleware.ClientIdClaim);
                var authorizationMode = context.User.FindFirstValue(
                    AgentTokenAuthenticationMiddleware.AuthorizationModeClaim);
                if (string.IsNullOrWhiteSpace(clientId) ||
                    !string.Equals(
                        authorizationMode,
                        "accessGrant",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status403Forbidden,
                        HostErrorCode.AccessDenied,
                        "This client does not use accessGrant authorization.");
                    return Results.Empty;
                }

                var plaintextSecret = context.Request.Headers[AccessGrantHeaders.Grant]
                    .ToString();
                if (string.IsNullOrWhiteSpace(plaintextSecret))
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status403Forbidden,
                        HostErrorCode.AccessGrantRequired,
                        "An Access Grant secret is required.");
                    return Results.Empty;
                }

                var status = await grants.RevokeAsync(
                    clientId,
                    grantId,
                    plaintextSecret);
                if (status == AccessGrantRevokeStatus.Invalid)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status403Forbidden,
                        HostErrorCode.AccessGrantInvalid,
                        "The Access Grant secret is invalid.");
                    return Results.Empty;
                }

                return Results.NoContent();
            });
        HostHealthData CreateHealthData()
        {
            return new HostHealthData(
                "ready",
                typeof(HostApplication).Assembly.GetName().Version?.ToString()
                    ?? "1.0.0",
                ProtocolVersion,
                hostInstanceId,
                configFingerprint);
        }

        app.MapGet(
            configuration.Endpoint.AdminPath + "/identity",
            () => Results.Json(
                new HostApiResponse<HostHealthData>(
                    true,
                    CreateHealthData())));
        app.MapGet(
            configuration.Endpoint.AdminPath + "/health",
            () => Results.Json(
                new HostApiResponse<HostHealthData>(
                    true,
                    CreateHealthData())));
        app.MapGet(
            configuration.Endpoint.AdminPath + "/access-grants",
            (AccessGrantService grants) =>
            {
                var items = grants.List()
                    .Select(HostDtoMapper.ToAccessGrantData)
                    .ToList();
                return Results.Json(
                    new HostApiResponse<IReadOnlyList<HostAccessGrantData>>(
                        true,
                        items));
            });
        app.MapDelete(
            configuration.Endpoint.AdminPath + "/access-grants/{grantId:guid}",
            async (
                Guid grantId,
                AccessGrantService grants,
                HttpContext context) =>
            {
                var status = await grants.RevokeByIdAsync(grantId);
                if (status == AccessGrantRevokeStatus.AlreadyRemoved)
                {
                    await HostHttpResponses.WriteErrorAsync(
                        context,
                        StatusCodes.Status404NotFound,
                        HostErrorCode.NotFound,
                        "Access Grant was not found.");
                    return Results.Empty;
                }
                return Results.NoContent();
            });
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
                    var requestId = context.Items.TryGetValue(
                            HostHttpContextKeys.RequestId,
                            out var value)
                        ? value as string
                        : null;
                    await clients.DeleteAsync(
                        clientId,
                        requestId,
                        cancellationToken);
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
                            HostAdminContext.GetActor(context),
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
                            HostAdminContext.GetActor(context),
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
                            HostAdminContext.GetActor(context),
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
                            HostAdminContext.GetActor(context),
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
                            HostAdminContext.GetActor(context),
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
                            HostAdminContext.GetActor(context),
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
                            HostAdminContext.GetActor(context),
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
            configuration.Endpoint.AdminPath + "/maintenance/backup",
            async (
                HostBackupRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteMaintenanceAsync(
                        (service, token) => service.CreateBackupAsync(
                            request.DestinationPath ?? string.Empty,
                            HostAdminContext.GetActor(context),
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultBackupResult>(true, result));
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
            configuration.Endpoint.AdminPath + "/maintenance/restore-new",
            async (
                HostRestoreNewRequest request,
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await sessions.RestoreBackupToNewAsync(
                        request.BackupPath ?? string.Empty,
                        request.TargetPath ?? string.Empty,
                        HostAdminContext.GetActor(context),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultRestoreBackupResult>(
                            true,
                            result));
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
            configuration.Endpoint.AdminPath + "/maintenance/verify",
            async (
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var report = await vault.ExecuteAsync(
                        (service, token) => service.VerifyVaultAsync(token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultIntegrityReport>(true, report));
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
            configuration.Endpoint.AdminPath + "/maintenance/password/change",
            async (
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await sessions.ChangePasswordAsync(
                        HostAdminContext.GetActor(context),
                        cancellationToken);
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
            configuration.Endpoint.AdminPath + "/maintenance/recovery/rotate",
            async (
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var recoveryKey = await sessions.RotateRecoveryKeyAsync(
                        HostAdminContext.GetActor(context),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<HostRecoveryRotationData>(
                            true,
                            new HostRecoveryRotationData(recoveryKey)));
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
            configuration.Endpoint.AdminPath + "/maintenance/recovery/confirm",
            async (
                HostSessionCoordinator sessions,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await sessions.ConfirmRecoveryKeyAsync(
                        HostAdminContext.GetActor(context),
                        cancellationToken);
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
            configuration.Endpoint.AdminPath + "/audit",
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
                        (service, token) => service.GetAuditPageAsync(
                            cursor,
                            limit ?? 50,
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultAuditPage>(true, page));
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
            configuration.Endpoint.AdminPath + "/maintenance/index/rebuild",
            async (
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var rebuilt = await vault.ExecuteMaintenanceAsync(
                        (service, token) => service.RebuildSearchIndexAsync(
                            HostAdminContext.GetActor(context),
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<int>(true, rebuilt));
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
            configuration.Endpoint.AdminPath + "/export",
            async (
                HostExportRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteMaintenanceAsync(
                        (service, token) => service.ExportSnapshotAsync(
                            request.DestinationPath ?? string.Empty,
                            HostAdminContext.GetActor(context),
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultExportResult>(true, result));
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
            configuration.Endpoint.AdminPath + "/import/preview",
            async (
                HostImportPreviewRequest request,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var preview = await vault.ExecuteMaintenanceAsync(
                        (service, token) => service.PreviewImportAsync(
                            request.SourceRoot ?? string.Empty,
                            HostAdminContext.GetActor(context),
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultImportPreview>(true, preview));
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
            configuration.Endpoint.AdminPath + "/import/{jobId:guid}/apply",
            async (
                Guid jobId,
                HostVaultGateway vault,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await vault.ExecuteMaintenanceAsync(
                        (service, token) => service.CommitImportAsync(
                            jobId,
                            HostAdminContext.GetActor(context),
                            token),
                        cancellationToken);
                    return Results.Json(
                        new HostApiResponse<VaultImportResult>(true, result));
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

    private static AccessGrantScope ParseAccessGrantScopes(
        IReadOnlyList<string>? scopes)
    {
        if (scopes is null || scopes.Count == 0)
        {
            return AccessGrantScope.Read;
        }

        var result = AccessGrantScope.None;
        foreach (var rawScope in scopes)
        {
            var scope = rawScope?.Trim().ToLowerInvariant();
            result |= scope switch
            {
                "read" => AccessGrantScope.Read,
                "write" => AccessGrantScope.Write,
                "delete" => AccessGrantScope.Delete,
                _ => throw new HostException(
                    HostErrorCode.InvalidInput,
                    "Access Grant scopes must be read, write or delete.")
            };
        }
        return AccessGrantScopes.Normalize(result);
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
