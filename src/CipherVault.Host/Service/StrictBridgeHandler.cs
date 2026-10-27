using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Grants;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Mcp;
using CipherVault.Host.Security;
using CipherVault.Host.Session;

namespace CipherVault.Host.Service;

public sealed class StrictBridgeHandler(
    HostSessionCoordinator sessions,
    AccessGrantService grants,
    IHostAccessGrantPrompt grantPrompt,
    StrictAdminVaultRpcProvider rpc,
    HostVaultGateway? vaultGateway = null) : IBridgeSessionHandler
{
    private readonly StrictAdminVaultRpcProvider _rpc = rpc;
    private readonly HostVaultGateway? _vaultGateway = vaultGateway;

    public async Task<BridgeIssuedGrant> RequestGrantAsync(
        BridgeProcessSession session,
        BridgeGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new AdminPipeRpcException(
                "bridge_session_expired",
                "The Bridge session has expired.");
        }
        var snapshot = await sessions.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.SessionId is null ||
            snapshot.HardExpiresAt is null ||
            snapshot.State is not (
                VaultSessionState.Unlocked or
                VaultSessionState.Expiring))
        {
            throw new AdminPipeRpcException(
                "auth_required",
                "Vault must be unlocked.");
        }
        if (!AccessGrantPathPolicy.TryParse(
                request.Scopes,
                out var requestedScopes,
                out var requestedPaths))
        {
            throw new AdminPipeRpcException(
                "invalid_input",
                "Bridge scope must use read, write, delete, or action:folder.");
        }

        var outcome = await grants.RequestAsync(
            session.ClientId,
            session.BridgeInstanceId,
            session.AccessMode,
            requestedScopes,
            requestedPaths,
            new AccessGrantVaultGeneration(
                snapshot.SessionId.Value,
                snapshot.HardExpiresAt.Value < session.ExpiresAt
                    ? snapshot.HardExpiresAt.Value
                    : session.ExpiresAt),
            async (approval, token) =>
            {
                var prompt = await grantPrompt.RequestAsync(
                    new HostAccessGrantPromptRequest(
                        approval.ClientId,
                        approval.ClientInstanceId,
                        approval.TokenAccessMode,
                        approval.RequestedScopes,
                        approval.AllowedScopes,
                        approval.PathPolicy,
                        grants.IdleLifetime,
                        grants.HardLifetime),
                    token).ConfigureAwait(false);
                return new AccessGrantApprovalResult(
                    prompt.Approved,
                    prompt.ApprovedScopes,
                    prompt.Approved ? null : "user-denied");
            },
            session.ProcessSessionId,
            cancellationToken).ConfigureAwait(false);
        if (outcome.IssuedGrant is null)
        {
            throw MapRequestFailure(outcome);
        }

        var record = outcome.IssuedGrant.Record;
        var context = new AccessGrantContext(
            record.GrantId,
            record.ClientId,
            record.ClientInstanceId,
            record.Scopes,
            record.VaultSessionId!.Value,
            record.VerifiedProcessSessionId,
            record.PathPolicy);
        return new BridgeIssuedGrant(
            new BridgeGrantData(
                record.GrantId,
                record.ClientId,
                session.AccessMode == McpAccessMode.ReadWrite
                    ? "readWrite"
                    : "readOnly",
                record.PathPolicy!.ToScopeNames(record.Scopes)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                record.HardExpiresAt!.Value),
            context);
    }

    public async Task<string> CallToolAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        BridgeToolCallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return BridgeToolEnvelope.Error(
                "bridge_session_expired",
                "The Bridge session has expired.");
        }
        if (string.Equals(
                request.ToolName,
                "vault_status",
                StringComparison.Ordinal))
        {
            var vaultSnapshot = await sessions.GetSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            return BridgeToolEnvelope.VaultStatus(
                session.ClientId,
                session.AccessMode,
                vaultSnapshot);
        }
        await using var lease = await sessions.AcquireLeaseAsync(
            cancellationToken).ConfigureAwait(false);
        using var leaseScope = _vaultGateway?.UseLease(lease);
        try
        {
            await grants.EnsureClockAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VaultException exception) when (
            exception.Code == VaultErrorCode.ClockRollback)
        {
            return await DeniedAsync(
                session,
                grant,
                request.ToolName,
                "clock_rollback",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException exception) when (
            exception.Code is VaultErrorCode.SessionExpired or
                VaultErrorCode.SessionLocked)
        {
            return await DeniedAsync(
                session,
                grant,
                request.ToolName,
                "auth_required",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
        if (grant is null)
        {
            return await DeniedAsync(
                session,
                grant: null,
                request.ToolName,
                "access_grant_required",
                "Request a Bridge Access Grant first.",
                cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await sessions.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.State is not (
                VaultSessionState.Unlocked or
                VaultSessionState.Expiring))
        {
            return await DeniedAsync(
                session,
                grant,
                request.ToolName,
                "auth_required",
                "Vault must be unlocked.",
                cancellationToken).ConfigureAwait(false);
        }
        if (snapshot.SessionId != grant.VaultSessionId)
        {
            return await DeniedAsync(
                session,
                grant,
                request.ToolName,
                "access_grant_revoked",
                "Vault session changed.",
                cancellationToken).ConfigureAwait(false);
        }
        if (!BridgeToolCatalog.TryGet(
                request.ToolName,
                out var definition))
        {
            return BridgeToolEnvelope.Error(
                "unsupported_tool",
                $"Unsupported bridge tool: {request.ToolName}");
        }
        if (!AccessGrantScopes.Includes(
                grant.Scopes,
                definition.RequiredScope))
        {
            return await DeniedAsync(
                session,
                grant,
                request.ToolName,
                "access_denied",
                "Access Grant does not allow this operation.",
                cancellationToken).ConfigureAwait(false);
        }

        var used = grants.PeekBridgeGrant(grant);
        if (used.Status != AccessGrantValidationStatus.Valid ||
            used.Grant is null)
        {
            return await DeniedAsync(
                session,
                grant,
                request.ToolName,
                used.Status == AccessGrantValidationStatus.Expired
                    ? "access_grant_expired"
                    : "access_grant_revoked",
                "Access Grant is no longer valid.",
                cancellationToken).ConfigureAwait(false);
        }
        grant = used.Grant;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var adminSession = new AdminSession(
                Guid.NewGuid(),
                session.ProcessSessionId,
                session.Client,
                session.OwnerSid,
                session.ServiceSid,
                now,
                now,
                now + TimeSpan.FromMinutes(15),
                now + TimeSpan.FromHours(4),
                grant.VaultSessionId,
                AdminSessionState.Active,
                RevocationReason: null,
                PathPolicy: grant.PathPolicy);
            var pathAccess = BridgePathAccess.From(grant);
            await EnsurePathAccessAsync(
                request.ToolName,
                request.Arguments,
                adminSession,
                pathAccess,
                cancellationToken).ConfigureAwait(false);
            var mapped = BridgeToolMapper.Map(
                definition,
                request.Arguments);
            var result = await _rpc.HandleAsync(
                adminSession,
                mapped.AdminMethod,
                mapped.AdminPayload,
                cancellationToken).ConfigureAwait(false);
            var resultElement = BridgePathAccessGuard.FilterResult(
                request.ToolName,
                BridgeToolEnvelope.SerializeResult(result),
                pathAccess);
            await grants.RecordSuccessfulUseAsync(
                grant,
                cancellationToken).ConfigureAwait(false);
            return definition.ResultKind switch
            {
                BridgeToolResultKind.Page =>
                    BridgeToolEnvelope.Page(resultElement),
                BridgeToolResultKind.Document =>
                    BridgeToolEnvelope.Document(resultElement),
                BridgeToolResultKind.Structured =>
                    BridgeToolEnvelope.Structured(resultElement),
                _ => throw new InvalidOperationException(
                    "Unsupported bridge tool result kind.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BridgeToolException exception)
        {
            if (exception.Code is "access_denied" or "not_found")
            {
                await DeniedAsync(
                    session,
                    grant,
                    request.ToolName,
                    exception.Code,
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);
            }
            return BridgeToolEnvelope.Error(
                exception.Code,
                exception.Message);
        }
        catch (AdminPipeRpcException exception)
        {
            return BridgeToolEnvelope.Error(
                exception.Code,
                exception.Message);
        }
        catch (HostException exception)
        {
            return BridgeToolEnvelope.Error(
                BridgeToolErrorMapper.ToCode(exception),
                exception.Message);
        }
        catch (VaultException exception)
        {
            return BridgeToolEnvelope.Error(
                BridgeToolErrorMapper.ToCode(exception),
                exception.Message,
                exception.Details);
        }
    }

    private async Task EnsurePathAccessAsync(
        string toolName,
        JsonElement arguments,
        AdminSession adminSession,
        BridgePathAccess access,
        CancellationToken cancellationToken)
    {
        if (!access.Restricted)
        {
            return;
        }
        if (toolName == "tag_list" && access.ReadRestricted)
        {
            throw new BridgeToolException(
                "access_denied",
                "tag_list is unavailable while using folder-scoped access.");
        }
        if (toolName == "create_note")
        {
            var targetFolder = OptionalString(arguments, "folder");
            if (string.IsNullOrWhiteSpace(targetFolder) ||
                !access.AllowsWrite(targetFolder))
            {
                throw new BridgeToolException(
                    "access_denied",
                    "The write scope does not allow this folder.");
            }
            return;
        }
        if (!BridgePathAccessGuard.RequiresDocumentLookup(toolName))
        {
            return;
        }
        if (!TryGetDocumentId(arguments, out var documentId))
        {
            throw new BridgeToolException(
                "invalid_input",
                "The documentId argument is required.");
        }
        var virtualPath = await GetVirtualPathAsync(
            adminSession,
            documentId,
            cancellationToken).ConfigureAwait(false);
        if (!access.AllowsRead(virtualPath))
        {
            throw new BridgeToolException(
                "not_found",
                "Document was not found.");
        }
        if (BridgePathAccessGuard.IsWriteTool(toolName) &&
            !access.AllowsWrite(virtualPath))
        {
            throw new BridgeToolException(
                "not_found",
                "Document was not found.");
        }
        if (toolName == "delete_note" &&
            !access.AllowsDelete(virtualPath))
        {
            throw new BridgeToolException(
                "not_found",
                "Document was not found.");
        }
        if (toolName == "update_note" &&
            OptionalString(arguments, "folder") is { } folder &&
            !access.AllowsWrite(folder))
        {
            throw new BridgeToolException(
                "access_denied",
                "The write scope does not allow the destination folder.");
        }
    }

    private async Task<string> GetVirtualPathAsync(
        AdminSession adminSession,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var result = await _rpc.HandleAsync(
            adminSession,
            "admin.notes.map",
            JsonSerializer.SerializeToElement(
                new AdminDocumentMapRequest(documentId, 0, 0, 0, 1)),
            cancellationToken).ConfigureAwait(false);
        return result is VaultDocumentMap map
            ? map.VirtualPath
            : throw new VaultException(
                VaultErrorCode.NotFound,
                "Document was not found.");
    }

    private static Guid? OptionalGuid(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out var result))
        {
            return null;
        }
        return result;
    }

    private static bool TryGetDocumentId(
        JsonElement arguments,
        out Guid documentId)
    {
        var id = OptionalGuid(arguments, "documentId");
        documentId = id ?? Guid.Empty;
        return id is not null && documentId != Guid.Empty;
    }

    private static string? OptionalString(
        JsonElement arguments,
        string name)
    {
        return arguments.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private async Task<string> DeniedAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        string toolName,
        string reasonCode,
        string message,
        CancellationToken cancellationToken)
    {
        await grants.AppendDeniedAccessAuditAsync(
            new AccessDeniedAudit(
                session.ClientId,
                session.BridgeInstanceId,
                session.ProcessSessionId,
                grant?.GrantId,
                grant?.VaultSessionId,
                reasonCode,
                grant is null
                    ? null
                    : string.Join(
                        ",",
                        AccessGrantScopes.ToNames(grant.Scopes)
                            .OrderBy(value => value, StringComparer.Ordinal)),
                RequestId: null),
            cancellationToken).ConfigureAwait(false);
        return BridgeToolEnvelope.Error(reasonCode, message);
    }

    public async Task<BridgeStatusData> GetStatusAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        _ = await sessions.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        var validation = grant is null
            ? null
            : grants.PeekBridgeGrant(grant);
        var active = validation?.Status == AccessGrantValidationStatus.Valid;
        var scopes = active && validation?.Grant is not null
            ? (validation.Grant.PathPolicy ??
                    AccessGrantPathPolicy.Unrestricted)
                .ToScopeNames(validation.Grant.Scopes)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
            : [];
        return new BridgeStatusData(
            active,
            session.ClientId,
            session.AccessMode == McpAccessMode.ReadWrite
                ? "readWrite"
                : "readOnly",
            session.ProcessSessionId,
            session.BridgeInstanceId,
            session.ExpiresAt,
            scopes,
            active ? validation?.Record?.HardExpiresAt : null);
    }

    public async Task CloseSessionAsync(
        Guid processSessionId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await grants.RevokeByProcessSessionAsync(
            processSessionId,
            reasonCode).ConfigureAwait(false);
    }

    private static AdminPipeRpcException MapRequestFailure(
        AccessGrantRequestOutcome outcome)
    {
        return outcome.Status switch
        {
            AccessGrantRequestStatus.Cooldown => new AdminPipeRpcException(
                "access_grant_cooldown",
                "Bridge Access Grant requests are temporarily blocked."),
            AccessGrantRequestStatus.Conflict => new AdminPipeRpcException(
                "conflict",
                outcome.ReasonCode == "client_identity_mismatch"
                    ? "Bridge client identity does not match the active Grant."
                    : "A Bridge Access Grant is already active."),
            _ => new AdminPipeRpcException(
                outcome.ReasonCode switch
                {
                    "vault-session-expired" => "auth_required",
                    "no_allowed_scopes" => "invalid_input",
                    "approval_broker_unavailable" => "approval_broker_unavailable",
                    "approval-cancelled" => "access_denied",
                    "approval-failed" => "access_denied",
                    "cancelled" => "access_denied",
                    _ => "access_denied"
                },
                outcome.ReasonCode ?? "Bridge Access Grant was denied.")
        };
    }
}
