using CipherVault.Core.Documents;

namespace CipherVault.Core.Grants;

public enum StoredAccessGrantState
{
    Active,
    Revoked,
    Expired
}

public sealed record StoredAccessGrant(
    Guid GrantId,
    string ClientId,
    string ClientDisplayName,
    Guid ClientInstanceId,
    Guid ProcessSessionId,
    Guid VaultId,
    Guid VaultSessionId,
    StoredAccessGrantState State,
    int Scopes,
    int RequestedScopes,
    string PathPolicyJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset ApprovedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset HardExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevocationReason,
    byte[] SecretHash);

public sealed record StoredAccessGrantQuery(
    string? State,
    string? Search,
    string? Cursor,
    int Limit);

public sealed record StoredAccessGrantCounts(
    int Total,
    int Active,
    int Revoked,
    int Expired);

public sealed record StoredAccessGrantPage(
    IReadOnlyList<StoredAccessGrant> Items,
    string? NextCursor,
    int TotalCount,
    StoredAccessGrantCounts Counts);

public enum StoredAccessGrantRevokeStatus
{
    Revoked,
    AlreadyRevoked,
    AlreadyExpired,
    NotFound
}

public sealed record StoredAccessGrantRevokeOutcome(
    Guid GrantId,
    StoredAccessGrantRevokeStatus Status,
    DateTimeOffset? RevokedAt,
    string? Reason);

public sealed record StoredAccessGrantRevokeAllOutcome(
    int RevokedCount,
    int AlreadyRevokedCount,
    int AlreadyExpiredCount);

public sealed record AccessDeniedAudit(
    string ClientId,
    Guid ClientInstanceId,
    Guid ProcessSessionId,
    Guid? GrantId,
    Guid? VaultSessionId,
    string ReasonCode,
    string? GrantScopes,
    string? RequestId);

public interface IAccessGrantStore
{
    Task CreateAsync(
        StoredAccessGrant grant,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default);

    Task<StoredAccessGrantPage> ListAsync(
        StoredAccessGrantQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<StoredAccessGrant?> GetAsync(
        Guid grantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredAccessGrant>> GetActiveGrantsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<StoredAccessGrantRevokeOutcome> RevokeAsync(
        Guid grantId,
        string reason,
        DateTimeOffset now,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default);

    Task<StoredAccessGrantRevokeAllOutcome> RevokeAllAsync(
        IReadOnlyCollection<Guid> activeGrantIds,
        string reason,
        DateTimeOffset now,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ExpireAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> RevokeStaleActiveAsync(
        string reason,
        DateTimeOffset now,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateLastUsedAsync(
        Guid grantId,
        DateTimeOffset usedAt,
        DateTimeOffset idleExpiresAt,
        CancellationToken cancellationToken = default);

    Task EnsureClockAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task AppendDeniedAccessAsync(
        AccessDeniedAudit audit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
