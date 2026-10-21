namespace CipherVault.Host.Session;

public sealed class HostVaultLease : IAsyncDisposable
{
    private Action? _release;

    internal HostVaultLease(
        Guid leaseId,
        Guid? sessionId,
        CancellationToken cancellationToken,
        Action release)
    {
        LeaseId = leaseId;
        SessionId = sessionId;
        CancellationToken = cancellationToken;
        _release = release;
    }

    public Guid LeaseId { get; }
    public Guid? SessionId { get; }
    public CancellationToken CancellationToken { get; }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}
