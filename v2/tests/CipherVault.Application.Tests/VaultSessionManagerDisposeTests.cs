using System.Security.Cryptography;
using CipherVault.Application.Vault;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerDisposeTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task DisposeFromUnlockedLocksSessionAndDoesNotShutdownSqlite()
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        var secondParent = CreateTemporaryDirectory();
        try
        {
            var manager = await VaultSessionManager.CreateAsync(vaultDirectory);
            await manager.InitializeAsync(Password);
            await manager.UnlockAsync(Password);
            var activeSession = manager.Session!;

            await manager.DisposeAsync();
            await manager.DisposeAsync();

            Assert.Null(manager.Session);
            Assert.Throws<ObjectDisposedException>(
                () => _ = activeSession.KeyMaterial.MasterKey);

            await using var second = await VaultSessionManager.CreateAsync(
                Path.Combine(secondParent, "vault"));
            await second.InitializeAsync(Password);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
            Directory.Delete(secondParent, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeWaitsForInFlightUnlockThenReleasesSession()
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        try
        {
            await CreateSlowInitializedVaultAsync(vaultDirectory);
            var manager = await VaultSessionManager.CreateAsync(vaultDirectory);

            var unlockTask = Task.Run(() => manager.UnlockAsync(Password));
            await WaitForStateAsync(manager, VaultSessionState.Unlocking);
            var concurrentUnlock = await Assert.ThrowsAsync<VaultException>(
                () => manager.UnlockAsync(Password));
            Assert.Equal(VaultErrorCode.VaultBusy, concurrentUnlock.Code);

            await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            var unlocked = await unlockTask.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(VaultSessionState.Unlocked, unlocked.State);
            Assert.Null(manager.Session);
        }
        finally
        {
            await TestDirectoryCleanup.DeleteBestEffortAsync(parent);
        }
    }

    [Fact]
    public async Task DisposeAfterLockFailedBestEffortReleasesOnceOperationStops()
    {
        var timing = new VaultSessionTimingOptions(
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(200));
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        try
        {
            var manager = await VaultSessionManager.CreateForTestAsync(
                vaultDirectory,
                options: null,
                timing,
                timeProvider: null);
            await manager.InitializeAsync(Password);
            await manager.UnlockAsync(Password);
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = Task.Run(async () =>
                await manager.ExecuteAsync<int>(async (_, _) =>
                {
                    started.SetResult();
                    await release.Task;
                    return 1;
                }));
            await started.Task;
            var lockStatus = await manager.LockAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(VaultSessionState.LockFailed, lockStatus.State);

            release.SetResult();
            var operationException = await Assert.ThrowsAsync<VaultException>(
                async () => await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(VaultErrorCode.VaultLockFailed, operationException.Code);

            await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(manager.Session);
        }
        finally
        {
            await TestDirectoryCleanup.DeleteBestEffortAsync(parent);
        }
    }

    private static async Task CreateSlowInitializedVaultAsync(string vaultDirectory)
    {
        Directory.CreateDirectory(vaultDirectory);
        var creation = VaultKeyService.Create(Password, Guid.NewGuid());
        byte[]? kek = null;
        try
        {
            var parameters = new VaultKdfParameters(
                "argon2id",
                MemoryKiB: 65536,
                Iterations: 10,
                Parallelism: 1,
                Salt: RandomNumberGenerator.GetBytes(VaultKdfParameters.SaltBytes));
            kek = Argon2PasswordDeriver.DeriveKek(Password, parameters);
            var envelope = creation.Envelope with
            {
                Kdf = parameters,
                KeyWrap = MasterKeyProtector.Wrap(
                    creation.KeyMaterial.MasterKey.Span,
                    kek,
                    creation.Envelope.VaultId,
                    parameters)
            };
            await VaultKeyEnvelopeSerializer.WriteAtomicAsync(
                Path.Combine(vaultDirectory, "vault.key"),
                envelope);
            await SqliteMcVaultStore.InitializeAsync(
                vaultDirectory,
                creation.KeyMaterial);
        }
        finally
        {
            if (kek is not null)
            {
                CryptographicOperations.ZeroMemory(kek);
            }

            creation.KeyMaterial.Dispose();
        }
    }

    private static async Task WaitForStateAsync(
        VaultSessionManager manager,
        VaultSessionState expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (manager.State == expected)
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Vault did not reach state {expected}.");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-dispose-manager-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
