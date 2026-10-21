namespace CipherVault.Host.Contracts;

public sealed record HostRequestAccessGrantRequest(
    Guid ClientInstanceId,
    IReadOnlyList<string>? Scopes);

public sealed record HostAccessGrantData(
    Guid GrantId,
    string ClientId,
    Guid ClientInstanceId,
    IReadOnlyList<string> Scopes,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? IdleExpiresAt,
    DateTimeOffset? HardExpiresAt);

public sealed record HostIssuedAccessGrantData(
    HostAccessGrantData Grant,
    string Secret);
