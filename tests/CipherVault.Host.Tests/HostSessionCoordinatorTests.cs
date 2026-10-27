using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
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
    public async Task InitializeAcceptsEmptyStrictVaultLayout()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-init-layout-{Guid.NewGuid():N}");
        var vaultPath = Path.Combine(root, "Vault");
        Directory.CreateDirectory(vaultPath);
        File.WriteAllText(Path.Combine(vaultPath, "vault.mode"), "{}");
        await using var vault = new VaultService(new ActiveActivityProvider());
        var prompt = new HostTestFixture.TestCredentialPrompt();
        var coordinator = new HostSessionCoordinator(
            vault,
            vaultPath,
            prompt,
            new AccessGrantService(),
            new ApprovalStore(),
            new AdminSessionStore());
        try
        {
            var initialized = await coordinator.InitializeAsync(vaultPath);

            Assert.Equal(Path.GetFullPath(vaultPath), initialized.VaultPath);
            Assert.True(File.Exists(Path.Combine(vaultPath, "vault.mode")));
            Assert.True(File.Exists(Path.Combine(vaultPath, "vault.db")));
            Assert.True(File.Exists(Path.Combine(vaultPath, "vault.keys")));
            var unlocked = await coordinator.UnlockAsync(
                HostCredentialPreference.Password);
            Assert.Equal(VaultSessionState.Unlocked, unlocked.State);
        }
        finally
        {
            await vault.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InitializeRejectsNonEmptyStrictVaultLayout()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-init-layout-{Guid.NewGuid():N}");
        var vaultPath = Path.Combine(root, "Vault");
        Directory.CreateDirectory(vaultPath);
        File.WriteAllText(Path.Combine(vaultPath, "vault.mode"), "{}");
        File.WriteAllText(Path.Combine(vaultPath, "unexpected.txt"), "data");
        await using var vault = new VaultService(new ActiveActivityProvider());
        var coordinator = new HostSessionCoordinator(
            vault,
            vaultPath,
            new HostTestFixture.TestCredentialPrompt(),
            new AccessGrantService(),
            new ApprovalStore(),
            new AdminSessionStore());
        try
        {
            var exception = await Assert.ThrowsAsync<HostException>(
                () => coordinator.InitializeAsync(vaultPath));

            Assert.Equal(HostErrorCode.AlreadyExists, exception.Code);
        }
        finally
        {
            await vault.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
    public async Task StatusReadDoesNotRevokeCurrentAdminSessionWhileLocked()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        var adminSessions = fixture.App.Services.GetRequiredService<AdminSessionStore>();
        var session = adminSessions.Create(
            new VerifiedProcessIdentity(
                VerifiedProcessKind.AdminCli,
                123,
                DateTimeOffset.UtcNow,
                "S-1-5-21-1",
                "S-1-5-5-1-1",
                1,
                @"C:\Program Files\CipherVault\CipherVault.Cli.exe",
                new string('a', 64),
                new string('b', 64),
                "CN=CipherVault Test",
                IntegrityLevel: 0x2000),
            "S-1-5-21-1",
            "S-1-5-80-1");

        _ = await coordinator.GetSnapshotAsync();

        Assert.Equal(
            AdminSessionState.Active,
            adminSessions.List().Single(item =>
                item.SessionId == session.SessionId).State);
    }

    [Fact]
    public async Task UnlockDoesNotRevokeCurrentAdminSessionWhileLocked()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<HostSessionCoordinator>();
        var adminSessions = fixture.App.Services.GetRequiredService<AdminSessionStore>();
        var session = adminSessions.Create(
            new VerifiedProcessIdentity(
                VerifiedProcessKind.AdminCli,
                123,
                DateTimeOffset.UtcNow,
                "S-1-5-21-1",
                "S-1-5-5-1-1",
                1,
                @"C:\Program Files\CipherVault\CipherVault.Cli.exe",
                new string('a', 64),
                new string('b', 64),
                "CN=CipherVault Test",
                IntegrityLevel: 0x2000),
            "S-1-5-21-1",
            "S-1-5-80-1");

        _ = await coordinator.UnlockAsync(HostCredentialPreference.Password);

        Assert.Equal(
            AdminSessionState.Active,
            adminSessions.List().Single(item =>
                item.SessionId == session.SessionId).State);
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
    public async Task GrantMutationWaitsForLeaseAndBlocksNewLeases()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        await coordinator.UnlockAsync(HostCredentialPreference.Password);
        var lease = await coordinator.AcquireLeaseAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = coordinator.ExecuteGrantMutationAsync(
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return 42;
            });

        await Task.Delay(100);
        Assert.False(mutation.IsCompleted);
        await Assert.ThrowsAsync<HostException>(() =>
            coordinator.AcquireLeaseAsync());

        await lease.DisposeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.Equal(42, await mutation);

        await using var after = await coordinator.AcquireLeaseAsync();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task GrantMutationWaitsForRealDocumentOperation(
        string operation)
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        var gateway = fixture.App.Services.GetRequiredService<
            HostVaultGateway>();
        await coordinator.UnlockAsync(HostCredentialPreference.Password);
        var created = await gateway.ExecuteAsync(
            (vault, token) => vault.CreateNoteAsync(
                "Concurrent Grant Mutation",
                "notes",
                "md",
                "# Concurrent Grant Mutation",
                null,
                VaultActorKind.System,
                token));
        var lease = await coordinator.AcquireLeaseAsync();
        using var scope = gateway.UseLease(lease);
        var mutationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = coordinator.ExecuteGrantMutationAsync(
            _ =>
            {
                mutationEntered.TrySetResult();
                return Task.FromResult(42);
            });
        await Task.Delay(150);
        Assert.False(mutation.IsCompleted);
        Assert.False(mutationEntered.Task.IsCompleted);

        object result = operation switch
        {
            "read" => await gateway.ExecuteAsync(
                (vault, token) => vault.ReadNoteAsync(
                    created.DocumentId,
                    offset: 0,
                    maxChars: 1_000,
                    token)),
            "write" => await gateway.ExecuteAsync(
                (vault, token) => vault.UpdateNoteAsync(
                    created.DocumentId,
                    expectedRevision: 1,
                    new VaultNotePatch(
                        Title: "Concurrent Grant Mutation Updated",
                        Content: "# Concurrent Grant Mutation Updated"),
                    VaultActorKind.System,
                    token)),
            "delete" => await gateway.ExecuteAsync(
                (vault, token) => vault.DeleteNoteAsync(
                    created.DocumentId,
                    expectedRevision: 1,
                    VaultActorKind.System,
                    token)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Assert.NotNull(result);
        Assert.False(mutation.IsCompleted);

        scope.Dispose();
        await lease.DisposeAsync();
        await mutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, await mutation);
        await using var after = await coordinator.AcquireLeaseAsync();
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

    [Fact]
    public async Task LockPersistsGrantRevocationAcrossReopen()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        var grants = fixture.App.Services.GetRequiredService<
            AccessGrantService>();
        var first = await coordinator.UnlockAsync(
            HostCredentialPreference.Password);
        var now = DateTimeOffset.UtcNow;
        var issued = await grants.RequestAsync(
            "mcp",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            new AccessGrantVaultGeneration(
                Assert.IsType<Guid>(first.SessionId),
                now.AddHours(1)),
            (_, _) => Task.FromResult(
                new AccessGrantApprovalResult(
                    true,
                    AccessGrantScope.Read)),
            Guid.NewGuid());
        Assert.Equal(AccessGrantRequestStatus.Approved, issued.Status);
        var record = issued.IssuedGrant!.Record;
        var context = new AccessGrantContext(
            record.GrantId,
            record.ClientId,
            record.ClientInstanceId,
            record.Scopes,
            record.VaultSessionId!.Value,
            record.VerifiedProcessSessionId,
            record.PathPolicy);

        await coordinator.LockAsync("test-lock");
        Assert.Equal(
            AccessGrantValidationStatus.Revoked,
            grants.UseBridgeGrant(context).Status);
        await coordinator.UnlockAsync(HostCredentialPreference.Password);

        var page = await grants.ListStoredAsync(
            state: "revoked",
            search: "mcp",
            cursor: null,
            limit: 50);
        var item = Assert.Single(page.Items);
        var detail = await grants.GetStoredAsync(item.GrantId);
        Assert.Equal("revoked", detail.State);
        Assert.Equal("vault-locked", detail.RevocationReason);
    }

    [Fact]
    public async Task SessionInitializationRevokesStaleActiveGrant()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var coordinator = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        var grants = fixture.App.Services.GetRequiredService<
            AccessGrantService>();
        var unlocked = await coordinator.UnlockAsync(
            HostCredentialPreference.Password);
        var now = DateTimeOffset.UtcNow;
        var issued = await grants.RequestAsync(
            "mcp",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            new AccessGrantVaultGeneration(
                Assert.IsType<Guid>(unlocked.SessionId),
                now.AddHours(1)),
            (_, _) => Task.FromResult(
                new AccessGrantApprovalResult(
                    true,
                    AccessGrantScope.Read)),
            Guid.NewGuid());
        Assert.Equal(AccessGrantRequestStatus.Approved, issued.Status);

        await grants.InitializeStoredSessionAsync();

        var detail = await grants.GetStoredAsync(
            issued.IssuedGrant!.Record.GrantId);
        Assert.Equal("revoked", detail.State);
        Assert.Equal("host-restarted", detail.RevocationReason);
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

    private sealed class ActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
