namespace CipherVault.Server.Api.Contracts;

public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?> Details,
    string CorrelationId);
