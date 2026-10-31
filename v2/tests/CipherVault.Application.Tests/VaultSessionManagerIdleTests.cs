using CipherVault.Application.Vault;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerIdleTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task IdleCheckBelowThresholdKeepsSessionUnlocked()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        time.Advance(TimeSpan.FromMinutes(9));

        var status = await fixture.Manager.CheckIdleAndLockAsync();

        Assert.Equal(VaultSessionState.Unlocked, status.State);
        Assert.NotNull(fixture.Manager.Session);
    }

    [Fact]
    public async Task IdleCheckAtThresholdLocksAndRecordsReason()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        time.Advance(TimeSpan.FromMinutes(10));

        var status = await fixture.Manager.CheckIdleAndLockAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Equal(VaultLockReason.IdleTimeout, status.LockReason);
        Assert.Null(fixture.Manager.Session);
    }

    [Fact]
    public async Task WallClockJumpsDoNotChangeMonotonicIdleDuration()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        time.MoveWallClock(TimeSpan.FromHours(3));
        time.MoveWallClock(TimeSpan.FromHours(-5));

        var status = await fixture.Manager.CheckIdleAndLockAsync();

        Assert.Equal(VaultSessionState.Unlocked, status.State);
    }

    [Fact]
    public async Task SleepLikeMonotonicAdvanceLocksOnNextCheck()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        time.Advance(TimeSpan.FromHours(2));

        var status = await fixture.Manager.GetStatusAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Null(fixture.Manager.Session);
    }

    [Fact]
    public async Task ActivityRefreshExtendsIdleDeadline()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        time.Advance(TimeSpan.FromMinutes(9));
        await fixture.Manager.TouchActivityAsync();
        time.Advance(TimeSpan.FromMinutes(2));

        var status = await fixture.Manager.CheckIdleAndLockAsync();

        Assert.Equal(VaultSessionState.Unlocked, status.State);
    }

    [Fact]
    public async Task StatusQueryAfterExpiryStartsLockWithoutRefreshingActivity()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.CreateAsync(time);
        await fixture.Manager.UnlockAsync(Password);
        var session = fixture.Manager.Session!;
        var before = session.Activity.Snapshot();
        time.Advance(TimeSpan.FromMinutes(11));

        var status = await fixture.Manager.GetStatusAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Equal(before, session.Activity.Snapshot());
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

        internal static async Task<Fixture> CreateAsync(TimeProvider timeProvider)
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
            $"cipher-vault-idle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
