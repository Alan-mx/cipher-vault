using CipherVault.Application.Vault;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerExecuteTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task ExecuteSerializesCallbacks()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () =>
            await fixture.Manager.ExecuteAsync<int>(async (_, _) =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                return 1;
            }));
        await firstEntered.Task;

        var second = Task.Run(async () =>
            await fixture.Manager.ExecuteAsync(
                (_, _) =>
                {
                    secondEntered.SetResult();
                    return ValueTask.FromResult(2);
                }));
        await Task.Delay(50);
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.SetResult();
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExecuteRefreshesActivityExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        var session = fixture.Manager.Session!;
        var before = session.Activity.Snapshot();

        var result = await fixture.Manager.ExecuteAsync(
            (_, _) => ValueTask.FromResult(7));

        var after = session.Activity.Snapshot();
        Assert.Equal(7, result);
        Assert.Equal(before.Generation + 1, after.Generation);
    }

    [Fact]
    public async Task ExecuteFailsBeforeCallbackWhenIdleExpired()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        time.Advance(TimeSpan.FromMinutes(11));
        var callbackInvoked = false;

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.Manager.ExecuteAsync(
                (_, _) =>
                {
                    callbackInvoked = true;
                    return ValueTask.FromResult(1);
                }));

        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);
        Assert.False(callbackInvoked);
        Assert.Equal(
            VaultSessionState.Locked,
            (await fixture.Manager.GetStatusAsync()).State);
    }

    [Fact]
    public async Task ExecuteRejectsNestedCallWithoutDeadlock()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);

        var exception = await Assert.ThrowsAsync<VaultException>(async () =>
            await fixture.Manager.ExecuteAsync(async (_, _) =>
            {
                await fixture.Manager.ExecuteAsync(
                    (_, _) => ValueTask.FromResult(1));
                return 0;
            }));

        Assert.Equal(VaultErrorCode.VaultBusy, exception.Code);
        Assert.Equal(
            VaultSessionState.Unlocked,
            (await fixture.Manager.GetStatusAsync()).State);
    }

    [Fact]
    public async Task ExecuteRejectsDatabaseBoundResult()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);

        var exception = await Assert.ThrowsAsync<VaultException>(async () =>
            await fixture.Manager.ExecuteAsync<object>(
                (context, _) => ValueTask.FromResult<object>(context)));

        Assert.Equal(VaultErrorCode.DatabaseError, exception.Code);
        Assert.Equal(
            VaultSessionState.Unlocked,
            (await fixture.Manager.GetStatusAsync()).State);
    }

    [Fact]
    public async Task CallerCancellationReleasesOperationGate()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Manager.UnlockAsync(Password);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Manager.ExecuteAsync(
                async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 1;
                },
                cancellation.Token));

        Assert.Equal(
            2,
            await fixture.Manager.ExecuteAsync(
                (_, _) => ValueTask.FromResult(2)));
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

        internal static async Task<Fixture> CreateAsync(
            TimeProvider? timeProvider = null)
        {
            var parent = CreateTemporaryDirectory();
            var manager = await VaultSessionManager.CreateAsync(
                Path.Combine(parent, "vault"),
                VaultSessionOptions.Default,
                timeProvider);
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
            Directory.Delete(Parent, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-execute-manager-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
