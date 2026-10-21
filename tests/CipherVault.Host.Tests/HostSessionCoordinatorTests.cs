using CipherVault.Core.Session;
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
}
