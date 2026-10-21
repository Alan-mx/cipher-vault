namespace CipherVault.Host.Session;

public sealed class HostClientConnection : IDisposable
{
    private Action? _release;

    internal HostClientConnection(Action release)
    {
        _release = release;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
