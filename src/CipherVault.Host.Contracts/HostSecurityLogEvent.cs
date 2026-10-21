namespace CipherVault.Host.Contracts;

public sealed record HostSecurityLogEvent(
    DateTimeOffset OccurredAt,
    string EventType,
    string? ClientId,
    Guid? ConnectionId,
    Guid? ClientInstanceId,
    Guid? GrantId,
    string? GrantScopes,
    string? RequestId,
    string Result,
    string? ReasonCode);
