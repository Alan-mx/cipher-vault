namespace CipherVault.Server.Mcp.Contracts;

public sealed record McpToolError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?> Details,
    string CorrelationId,
    string Hint,
    bool Retryable);
