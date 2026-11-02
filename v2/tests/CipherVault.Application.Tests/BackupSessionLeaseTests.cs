using System.Reflection;
using CipherVault.Application.Backup;
using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class BackupSessionLeaseTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task CredentialLifecycleKeepsBackupActivityUntilLeaseDispose()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = fixture.Manager.Session!;
        var lease = await fixture.Manager.BeginBackupSessionAsync();
        var leaseDisposed = false;
        try
        {
            var credential = lease.CreateDatabaseKeyCredential();
            var escapedView = credential.DatabaseKey;
            Assert.Equal(1, lease.ActiveCredentialCountForTest);
            Assert.True(session.HasActiveBackupOperationsForTest);

            var concurrent = Assert.Throws<VaultException>(
                () => lease.CreateDatabaseKeyCredential());
            Assert.Equal(VaultErrorCode.VaultBusy, concurrent.Code);

            var seal = Assert.Throws<VaultException>(
                () => lease.SealDatabaseKeyAccess());
            Assert.Equal(VaultErrorCode.VaultBusy, seal.Code);

            var dispose = await Assert.ThrowsAsync<VaultException>(
                async () => await lease.DisposeAsync());
            Assert.Equal(VaultErrorCode.VaultBusy, dispose.Code);
            Assert.True(session.HasActiveBackupOperationsForTest);

            credential.Dispose();
            credential.Dispose();
            Assert.Equal(0, lease.ActiveCredentialCountForTest);
            Assert.Throws<ObjectDisposedException>(
                () => _ = credential.DatabaseKey);

            lease.SealDatabaseKeyAccess();
            var sealedException = Assert.Throws<VaultException>(
                () => lease.CreateDatabaseKeyCredential());
            Assert.Equal(VaultErrorCode.VaultBusy, sealedException.Code);

            await lease.DisposeAsync();
            await lease.DisposeAsync();
            leaseDisposed = true;
            Assert.False(session.HasActiveBackupOperationsForTest);

            // A ReadOnlyMemory view cannot be revoked after it has escaped.
            Assert.Equal(32, escapedView.Length);
        }
        finally
        {
            if (!leaseDisposed)
            {
                try
                {
                    lease.SealDatabaseKeyAccess();
                }
                catch (ObjectDisposedException)
                {
                    // The assertion path already completed disposal.
                }

                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task RequestCancellationIsDistinctFromLockCancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var requestCancellation = new CancellationTokenSource();
        var requestLease = await fixture.Manager.BeginBackupSessionAsync(
            requestCancellation.Token);
        requestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ThrowLeaseCancellationAsync(requestLease));

        requestLease.SealDatabaseKeyAccess();
        await requestLease.DisposeAsync();

        var lockLease = await fixture.Manager.BeginBackupSessionAsync();
        var lockTask = fixture.Manager.LockAsync();
        await WaitForSessionCancellationAsync(
            fixture.Manager.Session!);

        var exception = Assert.Throws<VaultException>(
            () => lockLease.ThrowIfCancellationRequested());
        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);

        lockLease.SealDatabaseKeyAccess();
        await lockLease.DisposeAsync();
        var status = await lockTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VaultSessionState.Locked, status.State);
    }

    [Fact]
    public async Task LockWaitsForActiveLeaseBeforeReleasingKeyMaterial()
    {
        var timing = new VaultSessionTimingOptions(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.CreateAsync(timing);
        var session = fixture.Manager.Session!;
        var lease = await fixture.Manager.BeginBackupSessionAsync();
        var lockTask = fixture.Manager.LockAsync();
        await WaitForSessionCancellationAsync(session);

        await Task.Delay(100);
        Assert.False(lockTask.IsCompleted);
        Assert.Equal(32, session.KeyMaterial.DatabaseKey.Length);

        lease.SealDatabaseKeyAccess();
        await lease.DisposeAsync();
        var status = await lockTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Null(fixture.Manager.Session);
        Assert.Throws<ObjectDisposedException>(
            () => _ = session.KeyMaterial.DatabaseKey);
    }

    [Fact]
    public async Task CleanupFailureIsStickyAndEndsInLockFailed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var lease = await fixture.Manager.BeginBackupSessionAsync();
        lease.SealDatabaseKeyAccess();
        lease.MarkCleanupFailed(
            new IOException("intentional cleanup failure"));
        await lease.DisposeAsync();

        var status = await fixture.Manager.LockAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VaultSessionState.LockFailed, status.State);
        Assert.Null(fixture.Manager.Session);
        Assert.Equal(
            VaultErrorCode.VaultLockFailed,
            (await Assert.ThrowsAsync<VaultException>(
                () => fixture.Manager.UnlockAsync(Password))).Code);
    }

    [Fact]
    public async Task ReleaseRejectsActiveBackupWithoutClearingKeys()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = fixture.Manager.Session!;
        var lease = await fixture.Manager.BeginBackupSessionAsync();

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await session.ReleaseAsync());
        Assert.Equal(VaultErrorCode.VaultBusy, exception.Code);
        Assert.Equal(32, session.KeyMaterial.DatabaseKey.Length);

        lease.SealDatabaseKeyAccess();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task ZipStageDoesNotHoldDatabaseGate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.CreateBackupService(
            async stage =>
            {
                if (stage == BackupPackageStage.ZipStarted)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            });

        var backup = service.CreateAsync(
            new VaultActorContext(VaultActorKind.UserUi));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var query = fixture.Manager.ExecuteAsync(
            (_, _) => ValueTask.FromResult(1));
        Assert.Equal(1, await query.AsTask().WaitAsync(
            TimeSpan.FromSeconds(1)));

        release.TrySetResult();
        var result = await backup.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BackupFileStatus.Verified, result.Item.Status);
    }

    [Fact]
    public async Task LockDuringZipWaitsForLeaseAndPreventsPublish()
    {
        var timing = new VaultSessionTimingOptions(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.CreateAsync(timing);
        var session = fixture.Manager.Session!;
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.CreateBackupService(
            async stage =>
            {
                if (stage == BackupPackageStage.ZipStarted)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            });

        var backup = service.CreateAsync(
            new VaultActorContext(VaultActorKind.UserUi));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var lockTask = fixture.Manager.LockAsync();
        await WaitForSessionCancellationAsync(session);

        await Task.Delay(100);
        Assert.False(backup.IsCompleted);
        Assert.False(lockTask.IsCompleted);
        release.TrySetResult();

        var backupException = await Assert.ThrowsAsync<VaultException>(
            async () => await backup.WaitAsync(TimeSpan.FromSeconds(10)));
        var locked = await lockTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VaultErrorCode.VaultLocked, backupException.Code);
        Assert.Equal(VaultSessionState.Locked, locked.State);
        Assert.Empty(Directory.EnumerateFiles(
            fixture.BackupDirectory,
            "*.cvbackup"));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.BackupDirectory,
            "*.partial"));
    }

    [Fact]
    public async Task LockWinningBeforePublishCommitPreventsFinalFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.CreateBackupService(
            async stage =>
            {
                if (stage == BackupPackageStage.PublishCommitRequested)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            });

        var backup = service.CreateAsync(
            new VaultActorContext(VaultActorKind.UserUi));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var session = fixture.Manager.Session!;
        var lockTask = fixture.Manager.LockAsync();
        await WaitForSessionCancellationAsync(session);
        release.TrySetResult();

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await backup.WaitAsync(TimeSpan.FromSeconds(10)));
        var status = await lockTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);
        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Empty(Directory.EnumerateFiles(
            fixture.BackupDirectory,
            "*.cvbackup"));
    }

    [Fact]
    public async Task GrantedPublishCommitCompletesBeforeLockReleases()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.CreateBackupService(
            async stage =>
            {
                if (stage == BackupPackageStage.PublishCommitted)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            });

        var backup = service.CreateAsync(
            new VaultActorContext(VaultActorKind.UserUi));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var session = fixture.Manager.Session!;
        var lockTask = fixture.Manager.LockAsync();
        await WaitForSessionCancellationAsync(session);

        await Task.Delay(100);
        Assert.False(lockTask.IsCompleted);
        var finalFilesBeforeRelease = Directory.GetFiles(
            fixture.BackupDirectory,
            "*.cvbackup");
        Assert.Single(finalFilesBeforeRelease);

        release.TrySetResult();
        var result = await backup.WaitAsync(TimeSpan.FromSeconds(10));
        var status = await lockTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BackupFileStatus.Verified, result.Item.Status);
        Assert.False(result.AuditRecorded);
        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Single(Directory.GetFiles(
            fixture.BackupDirectory,
            "*.cvbackup"));
    }

    [Fact]
    public async Task ConcurrentLeaseDisposeDecrementsActivityExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = fixture.Manager.Session!;
        var first = await fixture.Manager.BeginBackupSessionAsync();
        var second = await fixture.Manager.BeginBackupSessionAsync();
        first.SealDatabaseKeyAccess();
        second.SealDatabaseKeyAccess();

        await Task.WhenAll(
            first.DisposeAsync().AsTask(),
            second.DisposeAsync().AsTask(),
            first.DisposeAsync().AsTask(),
            second.DisposeAsync().AsTask());

        Assert.False(session.HasActiveBackupOperationsForTest);
    }

    [Fact]
    public async Task SnapshotContractContainsNoDatabaseKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var properties = typeof(BackupSnapshotCapture)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(
            properties,
            property => property.Name.Contains(
                "DatabaseKey",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(BackupService).Assembly.GetTypes(),
            type => type.Name == "SensitiveDatabaseKeyLease");
        var credentialType = typeof(BackupPackageVerifier).Assembly.GetType(
            "CipherVault.Core.Backup.SnapshotDatabaseKeyCredential");
        Assert.NotNull(credentialType);
        Assert.False(credentialType.IsPublic);
    }

    private static async Task ThrowLeaseCancellationAsync(
        VaultBackupSessionLease lease)
    {
        lease.ThrowIfCancellationRequested();
        await Task.CompletedTask;
    }

    private static async Task WaitForSessionCancellationAsync(
        VaultSession session)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (session.OperationCancellation.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            "Vault Session cancellation was not observed.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string parent,
            VaultSessionManager manager,
            TimeProvider timeProvider)
        {
            Parent = parent;
            Manager = manager;
            TimeProvider = timeProvider;
            BackupDirectory = Path.Combine(parent, "backups");
        }

        internal string Parent { get; }

        internal VaultSessionManager Manager { get; }

        internal TimeProvider TimeProvider { get; }

        internal string BackupDirectory { get; }

        internal BackupService CreateBackupService(
            BackupPackageStageObserver? observer) =>
            new(
                new BackupStorageOptions(BackupDirectory),
                Manager,
                TimeProvider,
                observer);

        internal static Task<Fixture> CreateAsync() =>
            CreateAsync(VaultSessionTimingOptions.Default);

        internal static async Task<Fixture> CreateAsync(
            VaultSessionTimingOptions timing)
        {
            var parent = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-backup-lease-{Guid.NewGuid():N}");
            Directory.CreateDirectory(parent);
            var timeProvider = new ManualTimeProvider();
            var manager = await VaultSessionManager.CreateForTestAsync(
                Path.Combine(parent, "vault"),
                options: null,
                timing,
                timeProvider);
            try
            {
                await manager.InitializeAsync(Password);
                await manager.UnlockAsync(Password);
                return new Fixture(parent, manager, timeProvider);
            }
            catch
            {
                await manager.DisposeAsync();
                await TestDirectoryCleanup.DeleteBestEffortAsync(parent);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            await TestDirectoryCleanup.DeleteBestEffortAsync(Parent);
        }
    }
}
