using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using System.Text.Json;
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
    public async Task LockRevokesApprovalsAndAdminSessions()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        var approvals = fixture.App.Services.GetRequiredService<ApprovalStore>();
        var adminSessions = fixture.App.Services.GetRequiredService<AdminSessionStore>();
        var grants = fixture.App.Services.GetRequiredService<AccessGrantService>();
        var operation = approvals.Prepare(
            ApprovalOperationKind.PurgeNote,
            Guid.NewGuid(),
            vaultSessionId: null,
            JsonDocument.Parse("{}").RootElement.Clone());
        var adminSession = adminSessions.Create(
            new VerifiedProcessIdentity(
                VerifiedProcessKind.AdminUi,
                123,
                DateTimeOffset.UtcNow,
                "S-1-5-1",
                "S-1-5-2",
                1,
                "C:\\Program Files\\CipherVault\\CipherVault.Ui.Wpf.exe",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                SignerThumbprint: null,
                SignerSubject: null,
                IntegrityLevel: 0),
            "S-1-5-1",
            "S-1-5-2");

        Assert.Equal(AdminSessionState.Active, adminSession.State);
        await coordinator.UnlockAsync(HostCredentialPreference.Password);
        var snapshot = await coordinator.GetSnapshotAsync();
        var issued = await grants.RequestAsync(
            "bridge",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            new AccessGrantVaultGeneration(
                snapshot.SessionId!.Value,
                snapshot.HardExpiresAt!.Value),
            (_, _) => Task.FromResult(new AccessGrantApprovalResult(
                true,
                AccessGrantScope.Read)),
            Guid.NewGuid());
        Assert.Equal(AccessGrantRequestStatus.Approved, issued.Status);
        Assert.Single(grants.List());

        await coordinator.LockAsync("test-lock");

        Assert.Empty(grants.List());
        var consumed = approvals.Consume(
            operation.OperationId,
            ApprovalOperationKind.PurgeNote,
            operation.ProcessSessionId,
            operation.VaultSessionId,
            operation.ParametersHash);
        Assert.Equal(ApprovalConsumeStatus.NotApproved, consumed.Status);
        Assert.Null(consumed.Operation);
        Assert.Equal(
            AdminSessionState.Revoked,
            adminSessions.List().Single(item =>
                item.SessionId == adminSession.SessionId).State);
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

    [Fact]
    public async Task MaintenanceDrainsLeasesAndRejectsNewLeases()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        _ = await coordinator.UnlockAsync(HostCredentialPreference.Password);
        var lease = await coordinator.AcquireLeaseAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceTask = coordinator.ExecuteMaintenanceAsync(
            async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return 42;
            });

        await Assert.ThrowsAsync<HostException>(() =>
            coordinator.AcquireLeaseAsync());
        Assert.False(maintenanceTask.IsCompleted);

        await lease.DisposeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HostException>(() =>
            coordinator.AcquireLeaseAsync());

        release.TrySetResult();
        Assert.Equal(42, await maintenanceTask);
        await using var after = await coordinator.AcquireLeaseAsync();
    }

    [Fact]
    public async Task ConnectedClientCountTracksActiveMcpRequests()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();

        using (coordinator.EnterClient())
        {
            Assert.Equal(1, (await coordinator.GetSnapshotAsync()).ConnectedClients);
        }

        Assert.Equal(0, (await coordinator.GetSnapshotAsync()).ConnectedClients);
    }

    [Fact]
    public async Task UnlockWaitsForLeasesLeftByCoreAutoLock()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        var vault = fixture.App.Services.GetRequiredService<IVaultService>();

        var first = await coordinator.UnlockAsync(HostCredentialPreference.Password);
        var oldLease = await coordinator.AcquireLeaseAsync();
        await vault.LockAsync("simulated-core-auto-lock");

        var unlockTask = coordinator.UnlockAsync(HostCredentialPreference.Password);
        await Task.Delay(100);
        Assert.False(unlockTask.IsCompleted);

        await oldLease.DisposeAsync();
        var second = await unlockTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(VaultSessionState.Unlocked, second.State);
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
