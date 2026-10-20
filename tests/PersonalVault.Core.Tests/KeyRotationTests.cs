using PersonalVault.Core;
using PersonalVault.Core.Documents;
using PersonalVault.Core.Errors;
using PersonalVault.Core.Session;

namespace PersonalVault.Core.Tests;

public sealed class KeyRotationTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task ChangePasswordKeepsRecoveryKeyUsable()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        const string newPassword = "new correct horse battery staple";

        await fixture.Service.ChangePasswordAsync(
            fixture.VaultPath,
            Password,
            newPassword,
            VaultActorKind.UserCli);

        await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.UnlockAsync(
                fixture.VaultPath,
                Password,
                VaultCredentialType.Password));
        var unlocked = await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            newPassword,
            VaultCredentialType.Password);
        Assert.Equal(VaultSessionState.Unlocked, unlocked.State);
        await fixture.Service.LockAsync();

        var recoveryUnlock = await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            fixture.RecoveryKey,
            VaultCredentialType.RecoveryKey);
        Assert.Equal(VaultSessionState.Unlocked, recoveryUnlock.State);
        Assert.False(File.Exists(Path.Combine(fixture.VaultPath, "vault.keys.bak")));
    }

    [Fact]
    public async Task RotateRecoveryKeyInvalidatesOldKeyAndKeepsPassword()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        var newRecoveryKey = await fixture.Service.RotateRecoveryKeyAsync(
            fixture.VaultPath,
            Password,
            VaultActorKind.UserCli);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            newRecoveryKey,
            VaultActorKind.UserCli);

        await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.UnlockAsync(
                fixture.VaultPath,
                fixture.RecoveryKey,
                VaultCredentialType.RecoveryKey));
        var newRecoveryUnlock = await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            newRecoveryKey,
            VaultCredentialType.RecoveryKey);
        Assert.Equal(VaultSessionState.Unlocked, newRecoveryUnlock.State);
        await fixture.Service.LockAsync();

        var passwordUnlock = await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        Assert.Equal(VaultSessionState.Unlocked, passwordUnlock.State);
    }

    private sealed class VaultFixture : IAsyncDisposable
    {
        private VaultFixture(
            string rootPath,
            string vaultPath,
            string recoveryKey,
            VaultService service)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            RecoveryKey = recoveryKey;
            Service = service;
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public string RecoveryKey { get; }
        public VaultService Service { get; }

        public static async Task<VaultFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"personal-vault-key-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var service = new VaultService(new AlwaysActiveActivityProvider());
            var result = await service.InitializeAsync(vaultPath, Password);
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                result.RecoveryKey,
                VaultActorKind.UserUi);
            return new VaultFixture(root, vaultPath, result.RecoveryKey, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
