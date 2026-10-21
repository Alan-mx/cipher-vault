using CipherVault.Core.Configuration;

namespace CipherVault.Host.Security;

public sealed record AuthenticatedHostClient(
    string ClientId,
    McpAccessMode AccessMode,
    AgentAuthorizationMode AuthorizationMode);
