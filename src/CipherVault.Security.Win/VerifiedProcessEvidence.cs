using CipherVault.Host.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Security.Win;

public sealed class VerifiedProcessEvidence : IDisposable
{
    private SafeProcessHandle? _processHandle;

    internal VerifiedProcessEvidence(
        SafeProcessHandle processHandle,
        VerifiedProcessIdentity identity)
    {
        _processHandle = processHandle;
        Identity = identity;
    }

    public VerifiedProcessIdentity Identity { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _processHandle, null)?.Dispose();
    }
}
