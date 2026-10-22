using CipherVault.Core.Configuration;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
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
                snapshot.HardExpiresAt.Value),
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
            AgentClientIdentityKind.VerifiedBridgeProcess,
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
            record.IdentityKind,
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

    public Task<string> CallToolAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        BridgeToolCallRequest request,
        CancellationToken cancellationToken)
    {
        _ = session;
        _ = grant;
        _ = cancellationToken;
        _ = _rpc;
        return Task.FromException<string>(new AdminPipeRpcException(
            "unsupported_tool",
            $"Bridge tool dispatch is not enabled: {request.ToolName}"));
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
