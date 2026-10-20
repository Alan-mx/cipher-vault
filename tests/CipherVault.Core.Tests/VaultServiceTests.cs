using CipherVault.Core;
using CipherVault.Core.Crypto;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using System.Security.Cryptography;

namespace CipherVault.Core.Tests;

public sealed class VaultServiceTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task InitializeCreatesVaultAndUnlocksWithPassword()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        var unlock = await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        Assert.Equal(VaultSessionState.Unlocked, unlock.State);
        Assert.NotNull(unlock.SessionId);
        Assert.True(File.Exists(Path.Combine(fixture.VaultPath, "vault.db")));
        Assert.True(File.Exists(Path.Combine(fixture.VaultPath, "vault.keys")));

        var locked = await fixture.Service.LockAsync();
        Assert.Equal(VaultSessionState.Locked, locked.State);
    }

    [Fact]
    public async Task RecoveryKeyUnlocksVault()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        var unlock = await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            fixture.RecoveryKey,
            VaultCredentialType.RecoveryKey);

        Assert.Equal(VaultSessionState.Unlocked, unlock.State);
    }

    [Fact]
    public async Task WrongPasswordDoesNotUnlockVault()
    {
        await using var fixture = await VaultFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.UnlockAsync(
                fixture.VaultPath,
                "this password is definitely wrong",
                VaultCredentialType.Password));

        Assert.Equal(VaultErrorCode.InvalidCredential, exception.Code);
        Assert.Equal(VaultSessionState.Locked, (await fixture.Service.GetStatusAsync()).State);
    }

    [Fact]
    public async Task InvalidRecoveryKeyChecksumDoesNotUnlockVault()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        var tampered = fixture.RecoveryKey[..^1] + (fixture.RecoveryKey[^1] == '0' ? '1' : '0');

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.UnlockAsync(
                fixture.VaultPath,
                tampered,
                VaultCredentialType.RecoveryKey));

        Assert.Equal(VaultErrorCode.InvalidCredential, exception.Code);
    }

    [Fact]
    public async Task SystemIdleExpiresSession()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        fixture.Activity.SystemIdleTime = TimeSpan.FromMinutes(16);
        fixture.Time.Advance(TimeSpan.FromMinutes(16));
        var status = await fixture.Service.GetStatusAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
        Assert.Equal("session-expired", status.LockReason);
    }

    [Fact]
    public async Task LockedWorkstationForcesLock()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        fixture.Activity.IsWorkstationLocked = true;
        var status = await fixture.Service.GetStatusAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
    }

    [Fact]
    public async Task HardLifetimeExpiresEvenWhenSystemIsActive()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        fixture.Time.Advance(TimeSpan.FromHours(4) + TimeSpan.FromSeconds(1));
        var status = await fixture.Service.GetStatusAsync();

        Assert.Equal(VaultSessionState.Locked, status.State);
    }

    [Fact]
    public async Task InitializeRefusesExistingDirectory()
    {
        await using var fixture = await VaultFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.InitializeAsync(fixture.VaultPath, Password));

        Assert.Equal(VaultErrorCode.AlreadyExists, exception.Code);
    }

    [Fact]
    public void RecoveryKeyRoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var formatted = RecoveryKeyCodec.Format(key);
        var parsed = RecoveryKeyCodec.Parse(formatted);

        Assert.True(CryptographicOperations.FixedTimeEquals(key, parsed));
        Assert.Contains('-', formatted);
    }

    private sealed class VaultFixture : IAsyncDisposable
    {
        private VaultFixture(
            string rootPath,
            string vaultPath,
            string recoveryKey,
            ManualActivityProvider activity,
            ManualTimeProvider time,
            VaultService service)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            RecoveryKey = recoveryKey;
            Activity = activity;
            Time = time;
            Service = service;
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public string RecoveryKey { get; }
        public ManualActivityProvider Activity { get; }
        public ManualTimeProvider Time { get; }
        public VaultService Service { get; }

        public static async Task<VaultFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var activity = new ManualActivityProvider();
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
            var service = new VaultService(activity, time);
            var result = await service.InitializeAsync(vaultPath, Password);
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                result.RecoveryKey,
                VaultActorKind.UserUi);
            return new VaultFixture(root, vaultPath, result.RecoveryKey, activity, time, service);
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

    private sealed class ManualActivityProvider : ISystemActivityProvider
    {
        public TimeSpan SystemIdleTime { get; set; }
        public bool IsWorkstationLocked { get; set; }

        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(SystemIdleTime, IsWorkstationLocked);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }
}
