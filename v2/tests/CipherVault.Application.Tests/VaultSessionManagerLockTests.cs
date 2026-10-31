using CipherVault.Application.Vault;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerLockTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task ManualLockReleasesSessionAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);
        var session = fixture.Manager.Session;

        var locked = await fixture.Manager.LockAsync();
        var repeated = await fixture.Manager.LockAsync();

        Assert.Equal(VaultSessionState.Locked, locked.State);
        Assert.Equal(VaultSessionState.Locked, repeated.State);
        Assert.Null(fixture.Manager.Session);
        Assert.NotNull(session);
        Assert.Throws<ObjectDisposedException>(() => _ = session!.KeyMaterial.MasterKey);
    }

    [Fact]
    public async Task CallerCancellationDoesNotStopSharedLockCleanup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(async () =>
            await fixture.Manager.ExecuteAsync<int>(async (_, cancellationToken) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return 1;
            }));
        await started.Task;

        using var callerCancellation = new CancellationTokenSource();
        var lockTask = fixture.Manager.LockAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await lockTask);

        release.SetResult();
        var operationException = await Assert.ThrowsAsync<VaultException>(
            async () => await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(VaultErrorCode.VaultLocked, operationException.Code);
        await WaitForStateAsync(fixture.Manager, VaultSessionState.Locked);
        Assert.Null(fixture.Manager.Session);
    }

    [Fact]
    public async Task OperationsDuringLockingAreRejectedWithoutWaiting()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);
        var barrier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.LockCleanupBarrierForTest = () => barrier.Task;

        var lockTask = fixture.Manager.LockAsync();
        try
        {
            await WaitForStateAsync(fixture.Manager, VaultSessionState.Locking);

            var unlockException = await Assert.ThrowsAsync<VaultException>(
                () => fixture.Manager.UnlockAsync(Password));
            Assert.Equal(VaultErrorCode.VaultBusy, unlockException.Code);
            var executeException = await Assert.ThrowsAsync<VaultException>(
                async () => await fixture.Manager.ExecuteAsync(
                    (_, _) => ValueTask.FromResult(1)));
            Assert.Equal(VaultErrorCode.VaultLocked, executeException.Code);
        }
        finally
        {
            barrier.TrySetResult();
        }

        var locked = await lockTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VaultSessionState.Locked, locked.State);
    }

    [Fact]
    public async Task LockFailsWhenActiveOperationIgnoresCancellation()
    {
        var timing = new VaultSessionTimingOptions(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        await using var fixture = await Fixture.CreateAsync(timing);
        var sessionBaseline = SqliteMcDatabaseSession.ActiveSessionCountForTest;
        Assert.Equal(0, sessionBaseline);
        await fixture.Manager.UnlockAsync(Password);
        Assert.Equal(sessionBaseline + 1, SqliteMcDatabaseSession.ActiveSessionCountForTest);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(async () =>
            await fixture.Manager.ExecuteAsync<int>(async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return 1;
            }));
        await started.Task;

        var failed = await fixture.Manager.LockAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VaultSessionState.LockFailed, failed.State);
        Assert.NotNull(fixture.Manager.Session);
        var retryUnlockException = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Manager.UnlockAsync(Password));
        Assert.Equal(VaultErrorCode.VaultLockFailed, retryUnlockException.Code);
        var retryLockException = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Manager.LockAsync());
        Assert.Equal(VaultErrorCode.VaultLockFailed, retryLockException.Code);

        release.SetResult();
        var operationException = await Assert.ThrowsAsync<VaultException>(
            async () => await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(VaultErrorCode.VaultLockFailed, operationException.Code);

        var failedSession = fixture.Manager.Session!;
        await fixture.Manager.DisposeAsync();
        Assert.Null(fixture.Manager.Session);
        Assert.True(failedSession.Database.IsDisposedForTest);
        Assert.Equal(
            System.Data.ConnectionState.Closed,
            failedSession.Database.ConnectionStateForTest);
        Assert.Equal(sessionBaseline, SqliteMcDatabaseSession.ActiveSessionCountForTest);
    }

    [Fact]
    public async Task ConcurrentLockRequestsObserveOneCleanupResult()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);
        var sessionId = fixture.Manager.Session!.SessionId;

        var first = fixture.Manager.LockAsync();
        var second = fixture.Manager.LockAsync();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, status =>
        {
            Assert.Equal(VaultSessionState.Locked, status.State);
            Assert.Null(status.SessionId);
        });
        Assert.Null(fixture.Manager.Session);
        Assert.NotEqual(Guid.Empty, sessionId);
    }

    private static async Task WaitForStateAsync(
        VaultSessionManager manager,
        VaultSessionState expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (manager.State == expected)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"Vault did not reach state {expected}; current state is {manager.State}.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string parent, VaultSessionManager manager)
        {
            Parent = parent;
            Manager = manager;
        }

        internal VaultSessionManager Manager { get; }

        private string Parent { get; }

        internal static Task<Fixture> CreateAsync() =>
            CreateAsync(VaultSessionTimingOptions.Default);

        internal static async Task<Fixture> CreateAsync(
            VaultSessionTimingOptions timing)
        {
            var parent = CreateTemporaryDirectory();
            var manager = await VaultSessionManager.CreateForTestAsync(
                Path.Combine(parent, "vault"),
                options: null,
                timing,
                timeProvider: null);
            try
            {
                await manager.InitializeAsync(Password);
                return new Fixture(parent, manager);
            }
            catch
            {
                await manager.DisposeAsync();
                Directory.Delete(parent, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            await TestDirectoryCleanup.DeleteBestEffortAsync(Parent);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
