using CipherVault.Application.Vault;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerInitializeTests
{
    private const string Password = "correct horse battery staple";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeInstallsCompleteVaultAndStaysLocked(bool useExistingEmptyDirectory)
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        if (useExistingEmptyDirectory)
        {
            Directory.CreateDirectory(vaultDirectory);
        }

        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(vaultDirectory);
            var status = await manager.InitializeAsync(Password);

            Assert.Equal(VaultSessionState.Locked, status.State);
            Assert.Null(status.SessionId);
            Assert.True(File.Exists(Path.Combine(vaultDirectory, "vault.key")));
            Assert.True(File.Exists(Path.Combine(vaultDirectory, "vault.db")));
            Assert.Empty(
                Directory.EnumerateDirectories(parent, ".cipher-vault-init-*"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task InitializedVaultReturnsLockedAfterManagerRestart()
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
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedInitializeIsRejectedWithoutRewritingVault()
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(vaultDirectory);
            await manager.InitializeAsync(Password);
            var keyPath = Path.Combine(vaultDirectory, "vault.key");
            var before = await File.ReadAllBytesAsync(keyPath);

            var exception = await Assert.ThrowsAsync<VaultException>(
                () => manager.InitializeAsync("another valid password"));

            Assert.Equal(VaultErrorCode.VaultAlreadyInitialized, exception.Code);
            Assert.Equal(before, await File.ReadAllBytesAsync(keyPath));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidPasswordLeavesVaultUninitializedAndCreatesNoArtifacts()
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(vaultDirectory);
            var exception = await Assert.ThrowsAsync<VaultException>(
                () => manager.InitializeAsync("short"));

            Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
            Assert.Equal(
                VaultSessionState.Uninitialized,
                (await manager.GetStatusAsync()).State);
            Assert.False(Directory.Exists(vaultDirectory));
            Assert.Empty(
                Directory.EnumerateDirectories(parent, ".cipher-vault-init-*"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringKdfCleansStagingAndReturnsToUninitialized()
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(vaultDirectory);
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(10));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.InitializeAsync(Password, cancellation.Token));

            Assert.Equal(
                VaultSessionState.Uninitialized,
                (await manager.GetStatusAsync()).State);
            Assert.False(Directory.Exists(vaultDirectory));
            Assert.Empty(
                Directory.EnumerateDirectories(parent, ".cipher-vault-init-*"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-initialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
