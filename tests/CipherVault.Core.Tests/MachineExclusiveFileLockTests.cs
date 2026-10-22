using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Tests;

public sealed class MachineExclusiveFileLockTests
{
    [Fact]
    public void ServicePathsExposeStableMachineLockNames()
    {
        var paths = CipherVaultServicePaths.Create(
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\owner\AppData\Local");

        Assert.Equal(
            Path.Combine(paths.LocksPath, "host.instance.lock"),
            paths.HostInstanceLockPath);
        Assert.Equal(
            Path.Combine(paths.LocksPath, "vault.owner.lock"),
            paths.VaultOwnerLockPath);
    }

    [Fact]
    public void SameMachineLockCannotBeAcquiredTwice()
    {
        using var fixture = new LockFixture();
        using var first = MachineExclusiveFileLock.Acquire(
            fixture.Paths.VaultOwnerLockPath);

        var exception = Assert.Throws<VaultException>(
            () => MachineExclusiveFileLock.Acquire(
                fixture.Paths.VaultOwnerLockPath));

        Assert.Equal(VaultErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public async Task DifferentMachineLocksAreIndependentAndDisposeIsIdempotent()
    {
        using var fixture = new LockFixture();
        var hostLock = MachineExclusiveFileLock.Acquire(
            fixture.Paths.HostInstanceLockPath);
        await using var vaultLock = MachineExclusiveFileLock.Acquire(
            fixture.Paths.VaultOwnerLockPath);

        hostLock.Dispose();
        hostLock.Dispose();

        Assert.NotNull(vaultLock);
    }

    private sealed class LockFixture : IDisposable
    {
        public LockFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-machine-lock-{Guid.NewGuid():N}");
            Paths = CipherVaultServicePaths.Create(
                Path.Combine(Root, "ProgramFiles"),
                Path.Combine(Root, "ProgramData"),
                Path.Combine(Root, "LocalAppData"));
        }

        public string Root { get; }
        public CipherVaultServicePaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
