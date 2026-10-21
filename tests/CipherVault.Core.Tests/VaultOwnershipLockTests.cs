using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Tests;

public sealed class VaultOwnershipLockTests
{
    [Fact]
    public async Task SecondAcquireFailsUntilFirstLockIsReleased()
    {
        using var fixture = new LockFixture();

        var first = await VaultOwnershipLock.AcquireAsync(fixture.VaultPath);
        try
        {
            var conflict = await Assert.ThrowsAsync<VaultException>(async () =>
                await VaultOwnershipLock.AcquireAsync(fixture.VaultPath));

            Assert.Equal(VaultErrorCode.Conflict, conflict.Code);
        }
        finally
        {
            await first.DisposeAsync();
        }

        await using var second = await VaultOwnershipLock.AcquireAsync(fixture.VaultPath);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task DifferentVaultPathsCanHoldIndependentOwnershipLocks()
    {
        using var fixture = new LockFixture();

        await using var first = await VaultOwnershipLock.AcquireAsync(
            Path.Combine(fixture.Root, "vault-a"));
        await using var second = await VaultOwnershipLock.AcquireAsync(
            Path.Combine(fixture.Root, "vault-b"));

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        using var fixture = new LockFixture();
        var ownership = await VaultOwnershipLock.AcquireAsync(fixture.VaultPath);

        await ownership.DisposeAsync();
        await ownership.DisposeAsync();
    }

    private sealed class LockFixture : IDisposable
    {
        public LockFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-ownership-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string VaultPath => Path.Combine(Root, "vault");

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
