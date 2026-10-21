using CipherVault.Core.Errors;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class HostUserInstanceLockTests
{
    [Fact]
    public void SecondLockForSameUserScopeFails()
    {
        var scope = Guid.NewGuid().ToString("N");
        using var first = HostUserInstanceLock.Acquire(scope);

        var exception = Assert.Throws<VaultException>(() =>
            HostUserInstanceLock.Acquire(scope));

        Assert.Equal(VaultErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public void DifferentUserScopesCanCoexist()
    {
        using var first = HostUserInstanceLock.Acquire(Guid.NewGuid().ToString("N"));
        using var second = HostUserInstanceLock.Acquire(Guid.NewGuid().ToString("N"));
    }
}
