namespace CipherVault.Host.Contracts;

public sealed record AdminAccessGrantListRequest(
    string? State,
    string? Search,
    string? Cursor,
    int Limit);

public sealed record AdminAccessGrantGetRequest(Guid GrantId);

public sealed record AdminAccessGrantRevokeRequest(Guid GrantId);

public sealed record AdminAccessGrantRevokeAllRequest(string PreviewToken);

public sealed record AdminAccessGrantStatusCounts(
    int Total,
    int Active,
    int Revoked,
    int Expired);

public sealed record AdminAccessGrantListItem(
    Guid GrantId,
    string ClientId,
    string ClientDisplayName,
    Guid ClientInstanceId,
    string State,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> ResourceScopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset HardExpiresAt,
    DateTimeOffset? LastUsedAt);

public sealed record AdminAccessGrantPage(
    IReadOnlyList<AdminAccessGrantListItem> Items,
    string? NextCursor,
    int TotalCount,
    AdminAccessGrantStatusCounts Counts);

public sealed record AdminAccessGrantDetail(
    Guid GrantId,
    string ClientId,
    string ClientDisplayName,
    Guid ClientInstanceId,
    string State,
    IReadOnlyList<string> RequestedOperations,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> ResourceScopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ApprovedAt,
    DateTimeOffset HardExpiresAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? RevocationReason);

public sealed record AdminAccessGrantRevokeResult(
    Guid GrantId,
    string Outcome,
    DateTimeOffset? RevokedAt,
    string? Reason);

public sealed record AdminAccessGrantImpactGroup(
    string ClientId,
    string ClientDisplayName,
    int ActiveCount,
    int PendingCount,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> ResourceScopes,
    bool IsTruncated = false);

public sealed record AdminAccessGrantRevokeAllPreview(
    string PreviewToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<AdminAccessGrantImpactGroup> Impacts,
    int ActiveCount,
    int PendingCount);

public sealed record AdminAccessGrantRevokeAllResult(
    int RevokedCount,
    int CancelledPendingCount,
    int AlreadyRevokedCount,
    int AlreadyExpiredCount);
