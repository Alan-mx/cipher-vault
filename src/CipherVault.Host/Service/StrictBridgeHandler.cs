using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
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
    StrictAdminVaultRpcProvider rpc) : IBridgeSessionHandler
{
    private readonly StrictAdminVaultRpcProvider _rpc = rpc;

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
        if (!TryParseScopes(request.Scopes, out var requestedScopes))
        {
            throw new AdminPipeRpcException(
                "invalid_input",
                "Bridge scope must be read, write or delete.");
        }

        var outcome = await grants.RequestAsync(
            session.ClientId,
            session.BridgeInstanceId,
            session.AccessMode,
            requestedScopes,
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
            record.VerifiedProcessSessionId);
        return new BridgeIssuedGrant(
            new BridgeGrantData(
                record.GrantId,
                record.ClientId,
                session.AccessMode == McpAccessMode.ReadWrite
                    ? "readWrite"
                    : "readOnly",
                AccessGrantScopes.ToNames(record.Scopes)
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
        if (grant is null)
        {
            return BridgeToolEnvelope.Error(
                "access_grant_required",
                "Request a Bridge Access Grant first.");
        }

        var snapshot = await sessions.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.State is not (
                VaultSessionState.Unlocked or
                VaultSessionState.Expiring))
        {
            return BridgeToolEnvelope.Error(
                "auth_required",
                "Vault must be unlocked.");
        }
        if (snapshot.SessionId != grant.VaultSessionId)
        {
            return BridgeToolEnvelope.Error(
                "access_grant_revoked",
                "Vault session changed.");
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
            return BridgeToolEnvelope.Error(
                "access_denied",
                "Access Grant does not allow this operation.");
        }

        var used = grants.UseBridgeGrant(grant);
        if (used.Status != AccessGrantValidationStatus.Valid ||
            used.Grant is null)
        {
            return BridgeToolEnvelope.Error(
                used.Status == AccessGrantValidationStatus.Expired
                    ? "access_grant_expired"
                    : "access_grant_revoked",
                "Access Grant is no longer valid.");
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
                RevocationReason: null);
            var mapped = BridgeToolMapper.Map(
                definition,
                request.Arguments);
            var result = await _rpc.HandleAsync(
                adminSession,
                mapped.AdminMethod,
                mapped.AdminPayload,
                cancellationToken).ConfigureAwait(false);
            var resultElement = BridgeToolEnvelope.SerializeResult(result);
            return definition.ResultKind == BridgeToolResultKind.Page
                ? BridgeToolEnvelope.Page(resultElement)
                : BridgeToolEnvelope.Document(resultElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BridgeToolException exception)
        {
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
                exception.Message);
        }
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
            ? AccessGrantScopes.ToNames(validation.Grant.Scopes)
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

    private static bool TryParseScopes(
        IReadOnlyList<string> values,
        out AccessGrantScope scopes)
    {
        scopes = AccessGrantScope.None;
        foreach (var value in values)
        {
            var normalized = value?.ToLowerInvariant();
            scopes |= normalized switch
            {
                "read" => AccessGrantScope.Read,
                "write" => AccessGrantScope.Write,
                "delete" => AccessGrantScope.Delete,
                _ => throw new AdminPipeRpcException(
                    "invalid_input",
                    "Bridge scope must be read, write or delete.")
            };
        }
        scopes = AccessGrantScopes.Normalize(scopes);
        return scopes != AccessGrantScope.None;
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
                    "approval-cancelled" => "access_denied",
                    "approval-failed" => "access_denied",
                    "cancelled" => "access_denied",
                    _ => "access_denied"
                },
                outcome.ReasonCode ?? "Bridge Access Grant was denied.")
        };
    }
}
