using CipherVault.Application.Vault;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerUnlockTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task CorrectPasswordPublishesOneSession()
    {
        await using var fixture = await Fixture.CreateAsync();

        var status = await fixture.Manager.UnlockAsync(Password);

        Assert.Equal(VaultSessionState.Unlocked, status.State);
        Assert.NotNull(status.SessionId);
        Assert.NotNull(status.StartedAtUtc);
        Assert.NotNull(status.LastActivityAtUtc);
        Assert.NotNull(fixture.Manager.Session);
        Assert.Equal(status.SessionId, fixture.Manager.Session!.SessionId);
    }

    [Fact]
    public async Task RepeatedUnlockReturnsCurrentSessionWithoutReplacement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Manager.UnlockAsync(Password);
        var firstSession = fixture.Manager.Session;

        var second = await fixture.Manager.UnlockAsync("wrong password is ignored");

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Same(firstSession, fixture.Manager.Session);
    }

    [Fact]
    public async Task WrongPasswordRemainsLockedWithoutSession()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Manager.UnlockAsync("wrong password is still long enough"));

        Assert.Equal(VaultErrorCode.InvalidCredential, exception.Code);
        Assert.Equal(
            VaultSessionState.Locked,
            (await fixture.Manager.GetStatusAsync()).State);
        Assert.Null(fixture.Manager.Session);
    }

    [Fact]
    public async Task UninitializedVaultCannotBeUnlocked()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(
                Path.Combine(parent, "vault"));

            var exception = await Assert.ThrowsAsync<VaultException>(
                () => manager.UnlockAsync(Password));

            Assert.Equal(VaultErrorCode.VaultNotInitialized, exception.Code);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task NewManagerStartsLockedAndCanUnlock()
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        try
        {
            await using (var first = await VaultSessionManager.CreateAsync(vaultDirectory))
            {
                await first.InitializeAsync(Password);
            }

            await using var restarted = await VaultSessionManager.CreateAsync(vaultDirectory);
            Assert.Equal(
                VaultSessionState.Locked,
                (await restarted.GetStatusAsync()).State);

            var unlocked = await restarted.UnlockAsync(Password);
            Assert.Equal(VaultSessionState.Unlocked, unlocked.State);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task TamperedEnvelopeFailsUnlockAndRemainsLocked()
    {
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.VaultDirectory, "vault.key"),
            "{}");

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Manager.UnlockAsync(Password));

        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
        Assert.Equal(
            VaultSessionState.Locked,
            (await fixture.Manager.GetStatusAsync()).State);
        Assert.Null(fixture.Manager.Session);
    }

    [Fact]
    public async Task LockOnUninitializedVaultIsRejected()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(
                Path.Combine(parent, "vault"));

            var exception = await Assert.ThrowsAsync<VaultException>(
                () => manager.LockAsync());

            Assert.Equal(VaultErrorCode.VaultNotInitialized, exception.Code);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string parent, VaultSessionManager manager)
        {
            Parent = parent;
            Manager = manager;
        }

        internal VaultSessionManager Manager { get; }

        internal string VaultDirectory => Path.Combine(Parent, "vault");

        private string Parent { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var parent = CreateTemporaryDirectory();
            var manager = await VaultSessionManager.CreateAsync(
                Path.Combine(parent, "vault"));
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
            $"cipher-vault-unlock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
