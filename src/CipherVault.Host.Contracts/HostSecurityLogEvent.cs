namespace CipherVault.Host.Contracts;

public sealed record HostSecurityLogEvent(
    DateTimeOffset OccurredAt,
    string EventType,
    string? ClientId,
    Guid? ConnectionId,
    string? RequestId,
    string Result,
    string? ReasonCode);
