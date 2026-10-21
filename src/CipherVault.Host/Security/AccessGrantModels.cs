using CipherVault.Core.Configuration;

namespace CipherVault.Host.Security;

[Flags]
public enum AccessGrantScope
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4
}

public enum AccessGrantState
{
    Pending,
    Active,
    Revoked,
    Expired
}

public sealed record AccessGrantVaultGeneration(
    Guid SessionId,
    DateTimeOffset HardExpiresAt);

public sealed record AccessGrantRecord(
    Guid GrantId,
    string ClientId,
    Guid ClientInstanceId,
    byte[]? SecretHash,
    AccessGrantScope Scopes,
    AccessGrantScope RequestedScopes,
    AccessGrantState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? IdleExpiresAt,
    DateTimeOffset? HardExpiresAt,
    Guid? VaultSessionId,
    string? DenialReason);

public sealed record IssuedAccessGrant(
    AccessGrantRecord Record,
    string PlaintextSecret);

public sealed record AccessGrantContext(
    Guid GrantId,
    string ClientId,
    Guid ClientInstanceId,
    AccessGrantScope Scopes,
    Guid VaultSessionId);

public readonly record struct AccessGrantSlotKey(
    string ClientId,
    Guid ClientInstanceId);

public sealed record AccessGrantCooldown(
    string ClientId,
    DateTimeOffset BlockedUntil,
    string ReasonCode);

public sealed record AccessGrantApprovalRequest(
    Guid GrantId,
    string ClientId,
    Guid ClientInstanceId,
    McpAccessMode TokenAccessMode,
    AccessGrantScope RequestedScopes,
    AccessGrantScope AllowedScopes);

public sealed record AccessGrantApprovalResult(
    bool Approved,
    AccessGrantScope Scopes = AccessGrantScope.None,
    string? DenialReason = null);

public enum AccessGrantRequestStatus
{
    Approved,
    Denied,
    Cooldown,
    Conflict
}

public sealed record AccessGrantRequestOutcome(
    AccessGrantRequestStatus Status,
    IssuedAccessGrant? IssuedGrant,
    DateTimeOffset? RetryAfter,
    string? ReasonCode);

public enum AccessGrantValidationStatus
{
    Valid,
    Missing,
    Invalid,
    Expired,
    Revoked
}

public enum AccessGrantRevokeStatus
{
    Revoked,
    AlreadyRemoved,
    Invalid
}

public static class AccessGrantHeaders
{
    public const string Grant = "X-CipherVault-Grant";
}

public sealed record AccessGrantValidationResult(
    AccessGrantValidationStatus Status,
    AccessGrantContext? Grant,
    string? ReasonCode);

public static class AccessGrantScopes
{
    public static AccessGrantScope Normalize(AccessGrantScope scopes)
    {
        if ((scopes & AccessGrantScope.Delete) != 0)
        {
            scopes |= AccessGrantScope.Write | AccessGrantScope.Read;
        }
        if ((scopes & AccessGrantScope.Write) != 0)
        {
            scopes |= AccessGrantScope.Read;
        }
        return scopes;
    }

    public static AccessGrantScope Ceiling(McpAccessMode accessMode)
    {
        return accessMode == McpAccessMode.ReadWrite
            ? AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete
            : AccessGrantScope.Read;
    }

    public static bool Includes(
        AccessGrantScope granted,
        AccessGrantScope required)
    {
        return (granted & required) == required;
    }

    public static IReadOnlySet<string> ToNames(AccessGrantScope scopes)
    {
        scopes = Normalize(scopes);
        var result = new HashSet<string>(StringComparer.Ordinal);
        if ((scopes & AccessGrantScope.Read) != 0)
        {
            result.Add("read");
        }
        if ((scopes & AccessGrantScope.Write) != 0)
        {
            result.Add("write");
        }
        if ((scopes & AccessGrantScope.Delete) != 0)
        {
            result.Add("delete");
        }
        return result;
    }
}
