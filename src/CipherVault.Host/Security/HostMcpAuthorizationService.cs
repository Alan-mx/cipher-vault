using System.Security.Claims;
using CipherVault.Core.Configuration;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Http;
using CipherVault.Host.Session;

namespace CipherVault.Host.Security;

public sealed record McpToolAuthorization(
    string ClientId,
    AgentAuthorizationMode AuthorizationMode,
    AccessGrantScope EffectiveScopes,
    AccessGrantContext? Grant);

public sealed class HostMcpAuthorizationService
{
    private readonly HostSessionCoordinator _sessions;
    private readonly AccessGrantService _grants;

    public HostMcpAuthorizationService(
        HostSessionCoordinator sessions,
        AccessGrantService grants)
    {
        _sessions = sessions;
        _grants = grants;
    }

    public async Task<McpToolAuthorization> ResolveAsync(
        ClaimsPrincipal principal,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var clientId = principal.FindFirstValue(
            AgentTokenAuthenticationMiddleware.ClientIdClaim);
        var accessMode = principal.FindFirstValue(
            AgentTokenAuthenticationMiddleware.AccessModeClaim);
        var authorizationModeValue = principal.FindFirstValue(
            AgentTokenAuthenticationMiddleware.AuthorizationModeClaim);
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(accessMode) ||
            string.IsNullOrWhiteSpace(authorizationModeValue))
        {
            throw new HostException(
                HostErrorCode.AccessDenied,
                "The agent client identity is unavailable.");
        }

        var authorizationMode = string.Equals(
                authorizationModeValue,
                "staticToken",
                StringComparison.OrdinalIgnoreCase)
            ? AgentAuthorizationMode.StaticToken
            : AgentAuthorizationMode.AccessGrant;
        if (authorizationMode == AgentAuthorizationMode.StaticToken)
        {
            return new McpToolAuthorization(
                clientId,
                authorizationMode,
                AccessGrantScope.Read,
                Grant: null);
        }

        var snapshot = await _sessions.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.State is not (
                VaultSessionState.Unlocked or
                VaultSessionState.Expiring) ||
            snapshot.SessionId is null ||
            snapshot.HardExpiresAt is null)
        {
            throw new HostException(
                HostErrorCode.SessionLocked,
                "Vault is locked.");
        }

        var plaintextGrant = httpContext.Request
            .Headers[AccessGrantHeaders.Grant]
            .ToString();
        var validation = await _grants.ValidateAsync(
            clientId,
            plaintextGrant,
            new AccessGrantVaultGeneration(
                snapshot.SessionId.Value,
                snapshot.HardExpiresAt.Value));
        switch (validation.Status)
        {
            case AccessGrantValidationStatus.Valid:
                return new McpToolAuthorization(
                    clientId,
                    authorizationMode,
                    validation.Grant!.Scopes,
                    validation.Grant);
            case AccessGrantValidationStatus.Missing:
                throw new HostException(
                    HostErrorCode.AccessGrantRequired,
                    "An Access Grant is required.");
            case AccessGrantValidationStatus.Expired:
                throw new HostException(
                    HostErrorCode.AccessGrantExpired,
                    "The Access Grant has expired.");
            case AccessGrantValidationStatus.Revoked:
                throw new HostException(
                    HostErrorCode.AccessGrantRevoked,
                    "The Access Grant has been revoked.");
            default:
                throw new HostException(
                    HostErrorCode.AccessGrantInvalid,
                    "The Access Grant is invalid.");
        }
    }
}
