namespace CipherVault.Server.Mcp;

public sealed class McpToolRequestException : Exception
{
    public McpToolRequestException(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ??
            new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Details { get; }
}
