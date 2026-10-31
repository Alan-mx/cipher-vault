using CipherVault.Application.Vault;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerActivityTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task TouchActivityRefreshesWithoutChangingState()
    {
        var timeProvider = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(timeProvider);
        await fixture.Manager.UnlockAsync(Password);
        var initial = fixture.Manager.Session!.Activity.Snapshot();
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        await fixture.Manager.TouchActivityAsync();

        var refreshed = fixture.Manager.Session!.Activity.Snapshot();
        Assert.True(refreshed.Generation > initial.Generation);
        Assert.True(refreshed.MonotonicTimestamp > initial.MonotonicTimestamp);
        Assert.Equal(
            VaultSessionState.Unlocked,
            (await fixture.Manager.GetStatusAsync()).State);
    }

    [Fact]
    public async Task StatusQueryDoesNotRefreshActivity()
    {
        var timeProvider = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(timeProvider);
        await fixture.Manager.UnlockAsync(Password);
        var initial = fixture.Manager.Session!.Activity.Snapshot();
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        _ = await fixture.Manager.GetStatusAsync();

        var after = fixture.Manager.Session!.Activity.Snapshot();
        Assert.Equal(initial, after);
    }

    [Fact]
    public async Task TouchActivityFailsWhenLocked()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.Manager.TouchActivityAsync());

        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);
    }

    [Fact]
    public async Task TouchActivityAfterTimeoutLocksInsteadOfRevivingSession()
    {
        var timeProvider = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(timeProvider);
        await fixture.Manager.UnlockAsync(Password);
        timeProvider.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.Manager.TouchActivityAsync());

        Assert.Equal(VaultErrorCode.VaultLocked, exception.Code);
        Assert.Equal(
            VaultSessionState.Locked,
            (await fixture.Manager.GetStatusAsync()).State);
        Assert.Null(fixture.Manager.Session);
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
            $"cipher-vault-activity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
