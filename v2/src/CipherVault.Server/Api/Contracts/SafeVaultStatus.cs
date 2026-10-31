using System.Text.Json.Serialization;

namespace CipherVault.Server.Api.Contracts;

public sealed record SafeVaultStatus(
    string State,
    bool IsInitialized,
    bool IsUnlocked,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    DateTimeOffset? IdleExpiresAtUtc,
    int IdleTimeoutMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    int? IdleRemainingSeconds,
    string LockReason);

public sealed record AntiforgeryTokenResponse(string Token);

public sealed record VaultSetupApiRequest(string Password, bool RiskAccepted);

public sealed record VaultUnlockApiRequest(string Password);

public sealed record VaultChangePasswordApiRequest(
    string OldPassword,
    string NewPassword);

public sealed record TrashRestoreApiRequest(string? ReplacementPath = null);

public sealed record PurgeApiResult(Guid? AuditEventId);
