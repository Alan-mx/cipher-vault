using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Session;

public sealed record BridgeIssuedGrant(
    BridgeGrantData Data,
    AccessGrantContext Context);

public interface IBridgeSessionHandler
{
    Task<BridgeIssuedGrant> RequestGrantAsync(
        BridgeProcessSession session,
        BridgeGrantRequest request,
        CancellationToken cancellationToken);

    Task<string> CallToolAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        BridgeToolCallRequest request,
        CancellationToken cancellationToken);

    Task<BridgeStatusData> GetStatusAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        CancellationToken cancellationToken);

    Task CloseSessionAsync(
        Guid processSessionId,
        string reasonCode,
        CancellationToken cancellationToken);
}
