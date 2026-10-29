using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Session;

public sealed record BridgeIssuedGrant(
    BridgeGrantData Data,
    AccessGrantContext Context);

public sealed class BridgeToolCallResult : IAsyncDisposable
{
    private IAsyncDisposable? _deliveryLease;

    private BridgeToolCallResult(
        string payload,
        IAsyncDisposable? deliveryLease)
    {
        Payload = payload;
        _deliveryLease = deliveryLease;
    }

    public string Payload { get; }

    public static BridgeToolCallResult WithoutLease(string payload)
    {
        return new BridgeToolCallResult(payload, deliveryLease: null);
    }

    public static BridgeToolCallResult WithLease(
        string payload,
        IAsyncDisposable deliveryLease)
    {
        ArgumentNullException.ThrowIfNull(deliveryLease);
        return new BridgeToolCallResult(payload, deliveryLease);
    }

    public ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref _deliveryLease, null);
        return lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

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

    async Task<BridgeToolCallResult> CallToolWithDeliveryAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        BridgeToolCallRequest request,
        CancellationToken cancellationToken)
    {
        var payload = await CallToolAsync(
            session,
            grant,
            request,
            cancellationToken).ConfigureAwait(false);
        return BridgeToolCallResult.WithoutLease(payload);
    }

    Task<BridgeStatusData> GetStatusAsync(
        BridgeProcessSession session,
        AccessGrantContext? grant,
        CancellationToken cancellationToken);

    Task CloseSessionAsync(
        Guid processSessionId,
        string reasonCode,
        CancellationToken cancellationToken);
}
