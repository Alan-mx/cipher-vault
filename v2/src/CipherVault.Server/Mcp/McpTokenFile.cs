namespace CipherVault.Server.Mcp;

public sealed record McpTokenFile(
    int Version,
    string HashAlgorithm,
    string Digest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset RotatedAtUtc);

public sealed record McpTokenStatus(
    bool Configured,
    int? Version,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? RotatedAtUtc);

public sealed record McpTokenRotateResponse(
    string Token,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset RotatedAtUtc);

internal sealed record McpTokenRuntimeState(
    bool Configured,
    int? Version,
    byte[] Digest,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? RotatedAtUtc)
{
    internal static McpTokenRuntimeState Unconfigured { get; } = new(
        false,
        null,
        [],
        null,
        null);
}
