using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using Microsoft.Extensions.DependencyInjection;

namespace CipherVault.Host.Tests;

public sealed class HostSessionCoordinatorTests
{
    [Fact]
    public async Task LockReturnsCompletedLockedSnapshot()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();

        var snapshot = await coordinator.LockAsync("test-lock");

        Assert.Equal(VaultSessionState.Locked, snapshot.State);
        Assert.Equal("test-lock", snapshot.LockReason);
        Assert.Equal(0, snapshot.ActiveLeases);
        Assert.Equal(0, snapshot.ConnectedClients);
    }

    [Fact]
    public async Task LockWaitsForActiveLeaseAndRejectsNewLeases()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        _ = await coordinator.UnlockAsync(HostCredentialPreference.Password);
        var lease = await coordinator.AcquireLeaseAsync();
        var lockTask = coordinator.LockAsync("barrier-test");

        await WaitUntilAsync(
            async () => (await coordinator.GetSnapshotAsync()).State ==
                VaultSessionState.Locking,
            TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HostException>(() =>
            coordinator.AcquireLeaseAsync());
        Assert.False(lockTask.IsCompleted);

        await lease.DisposeAsync();
        var locked = await lockTask;

        Assert.Equal(VaultSessionState.Locked, locked.State);
        Assert.Equal(0, locked.ActiveLeases);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }
            await Task.Delay(25);
        }
    }
}
