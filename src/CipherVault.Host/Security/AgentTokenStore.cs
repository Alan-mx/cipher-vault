using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

public sealed class AgentTokenStore
{
    private readonly IReadOnlyList<AgentClientPolicy> _clients;

    public AgentTokenStore(ResolvedCipherVaultHostConfig configuration)
    {
        _clients = configuration.Clients;
    }

    public Task<IReadOnlyList<HostClientData>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<HostClientData> result = _clients
            .Select(client => new HostClientData(
                client.ClientId,
                ToAccessMode(client.AccessMode),
                client.AllowLock,
                client.Enabled))
            .ToList();
        return Task.FromResult(result);
    }

    internal static string ToAccessMode(McpAccessMode accessMode)
    {
        return accessMode == McpAccessMode.ReadOnly
            ? "readOnly"
            : "readWrite";
    }
}
