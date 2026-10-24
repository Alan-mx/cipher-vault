using CipherVault.Host.Contracts;

namespace CipherVault.Host.Client;

public interface IBridgePipeClientConnection : IAsyncDisposable
{
    DateTimeOffset ExpiresAt { get; }

    bool IsUsable { get; }

    Task<BridgeGrantData> RequestGrantAsync(
        CancellationToken cancellationToken = default);

    Task<string> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);
}
