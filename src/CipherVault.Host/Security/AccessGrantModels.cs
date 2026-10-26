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

public sealed record AccessGrantPathPolicy(
    bool GlobalRead,
    IReadOnlyList<string> ReadPaths,
    bool GlobalWrite,
    IReadOnlyList<string> WritePaths,
    bool GlobalDelete,
    IReadOnlyList<string> DeletePaths)
{
    public static AccessGrantPathPolicy Unrestricted { get; } = new(
        true,
        [],
        true,
        [],
        true,
        []);

    public bool AllowsRead(string virtualPath)
    {
        return GlobalRead || Matches(ReadPaths, virtualPath);
    }

    public bool AllowsWrite(string virtualPath)
    {
        return GlobalWrite || Matches(WritePaths, virtualPath);
    }

    public bool AllowsDelete(string virtualPath)
    {
        return GlobalDelete || Matches(DeletePaths, virtualPath);
    }

    public bool HasScopedPaths =>
        !GlobalRead || !GlobalWrite || !GlobalDelete;

    public IReadOnlyList<string> ToScopeNames(AccessGrantScope scopes)
    {
        var result = new List<string>();
        if ((scopes & AccessGrantScope.Read) != 0)
        {
            if (GlobalRead)
            {
                result.Add("read");
            }
            else
            {
                result.AddRange(ReadPaths.Select(path => $"read:{path}"));
            }
        }
        if ((scopes & AccessGrantScope.Write) != 0)
        {
            if (GlobalWrite)
            {
                result.Add("write");
            }
            else
            {
                result.AddRange(WritePaths.Select(path => $"write:{path}"));
            }
        }
        if ((scopes & AccessGrantScope.Delete) != 0)
        {
            if (GlobalDelete)
            {
                result.Add("delete");
            }
            else
            {
                result.AddRange(DeletePaths.Select(path => $"delete:{path}"));
            }
        }
        return result;
    }

    public static bool TryParse(
        IReadOnlyList<string> values,
        out AccessGrantScope scopes,
        out AccessGrantPathPolicy paths)
    {
        scopes = AccessGrantScope.None;
        var globalRead = false;
        var globalWrite = false;
        var globalDelete = false;
        var readPaths = new HashSet<string>(StringComparer.Ordinal);
        var writePaths = new HashSet<string>(StringComparer.Ordinal);
        var deletePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                paths = Unrestricted;
                return false;
            }
            var parts = value.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
            var action = parts[0].ToLowerInvariant();
            if (parts.Length == 1)
            {
                switch (action)
                {
                    case "read":
                        globalRead = true;
                        scopes |= AccessGrantScope.Read;
                        break;
                    case "write":
                        globalWrite = true;
                        scopes |= AccessGrantScope.Write;
                        break;
                    case "delete":
                        globalDelete = true;
                        scopes |= AccessGrantScope.Delete;
                        break;
                    default:
                        paths = Unrestricted;
                        return false;
                }
                continue;
            }

            if (!TryNormalizeFolder(parts[1], out var folder))
            {
                paths = Unrestricted;
                return false;
            }
            switch (action)
            {
                case "read":
                    scopes |= AccessGrantScope.Read;
                    readPaths.Add(folder);
                    break;
                case "write":
                    scopes |= AccessGrantScope.Write;
                    readPaths.Add(folder);
                    writePaths.Add(folder);
                    break;
                case "delete":
                    scopes |= AccessGrantScope.Delete;
                    deletePaths.Add(folder);
                    readPaths.Add(folder);
                    writePaths.Add(folder);
                    break;
                default:
                    paths = Unrestricted;
                    return false;
            }
        }

        scopes = AccessGrantScopes.Normalize(scopes);
        if (globalWrite)
        {
            globalRead = true;
        }
        if (globalDelete)
        {
            globalRead = true;
            globalWrite = true;
        }
        paths = new AccessGrantPathPolicy(
            globalRead,
            readPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            globalWrite,
            writePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            globalDelete,
            deletePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        return scopes != AccessGrantScope.None;
    }

    public static bool TryNormalizeFolder(
        string value,
        out string normalized)
    {
        normalized = value
            .Trim()
            .Replace('\\', '/')
            .Trim('/')
            .ToLowerInvariant();
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            normalized = string.Empty;
            return false;
        }
        return true;
    }

    private static bool Matches(
        IReadOnlyList<string> allowedFolders,
        string virtualPath)
    {
        if (!TryNormalizeFolder(virtualPath, out var normalized) &&
            !TryNormalizePathLeaf(virtualPath, out normalized))
        {
            return false;
        }
        return allowedFolders.Any(folder =>
            string.Equals(normalized, folder, StringComparison.Ordinal) ||
            normalized.StartsWith(folder + "/", StringComparison.Ordinal));
    }

    private static bool TryNormalizePathLeaf(
        string value,
        out string normalized)
    {
        normalized = value
            .Trim()
            .Replace('\\', '/')
            .Trim('/')
            .ToLowerInvariant();
        return normalized.Length > 0 &&
            !normalized.Split('/').Any(part => part is "." or "..");
    }
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
    string? DenialReason,
    Guid VerifiedProcessSessionId,
    AccessGrantPathPolicy? PathPolicy = null);

public sealed record IssuedAccessGrant(
    AccessGrantRecord Record,
    string PlaintextSecret);

public sealed record AccessGrantContext(
    Guid GrantId,
    string ClientId,
    Guid ClientInstanceId,
    AccessGrantScope Scopes,
    Guid VaultSessionId,
    Guid VerifiedProcessSessionId,
    AccessGrantPathPolicy? PathPolicy = null);

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
    AccessGrantScope AllowedScopes,
    AccessGrantPathPolicy PathPolicy);

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

public sealed record AccessGrantValidationResult(
    AccessGrantValidationStatus Status,
    AccessGrantContext? Grant,
    string? ReasonCode,
    AccessGrantRecord? Record = null);

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
