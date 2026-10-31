using CipherVault.Core.Documents;

namespace CipherVault.Server.Mcp.Contracts;

public sealed record McpToolError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?> Details,
    string CorrelationId,
    string Hint,
    bool Retryable);

public sealed record DocumentMapToolResult(
    DocumentMap Map,
    bool ResponseTruncated,
    IReadOnlyList<string> TruncatedFields);
