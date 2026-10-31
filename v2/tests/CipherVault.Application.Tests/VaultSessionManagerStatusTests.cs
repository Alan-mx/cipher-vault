using CipherVault.Application.Vault;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class VaultSessionManagerStatusTests
{
    [Fact]
    public async Task MissingDirectoryIsUninitializedAndIsNotCreated()
    {
        var parent = CreateTemporaryDirectory();
        var vaultDirectory = Path.Combine(parent, "vault");
        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(vaultDirectory);
            var status = await manager.GetStatusAsync();

            Assert.Equal(VaultSessionState.Uninitialized, status.State);
            Assert.False(Directory.Exists(vaultDirectory));
            Assert.Null(status.SessionId);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyDirectoryIsUninitialized()
    {
        var vaultDirectory = CreateTemporaryDirectory();
        try
        {
            await using var manager = await VaultSessionManager.CreateAsync(vaultDirectory);

            Assert.Equal(
                VaultSessionState.Uninitialized,
                (await manager.GetStatusAsync()).State);
        }
        finally
        {
            Directory.Delete(vaultDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteLayoutIsLockedWithoutOpeningDatabase()
    {
        var vaultDirectory = CreateTemporaryDirectory();
        try
        {
            await CreateInitializedVaultAsync(vaultDirectory);
            await using var manager = await VaultSessionManager.CreateAsync(vaultDirectory);
            var status = await manager.GetStatusAsync();

            Assert.Equal(VaultSessionState.Locked, status.State);
            Assert.Null(status.SessionId);
            Assert.Equal(VaultLockReason.None, status.LockReason);
        }
        finally
        {
            Directory.Delete(vaultDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PartialLayoutIsRejectedAndPreserved(
        bool createKey,
        bool createDatabase)
    {
        var vaultDirectory = CreateTemporaryDirectory();
        var keyPath = Path.Combine(vaultDirectory, "vault.key");
        var databasePath = Path.Combine(vaultDirectory, "vault.db");
        try
        {
            if (createKey)
            {
                File.WriteAllText(keyPath, "{}");
            }

            if (createDatabase)
            {
                File.WriteAllBytes(databasePath, [1, 2, 3]);
            }

            var exception = await Assert.ThrowsAsync<VaultException>(
                () => VaultSessionManager.CreateAsync(vaultDirectory));

            Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
            Assert.Equal(createKey, File.Exists(keyPath));
            Assert.Equal(createDatabase, File.Exists(databasePath));
        }
        finally
        {
            Directory.Delete(vaultDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NonEmptyUninitializedDirectoryIsRejected()
    {
        var vaultDirectory = CreateTemporaryDirectory();
        var userFile = Path.Combine(vaultDirectory, "keep.txt");
        File.WriteAllText(userFile, "keep");
        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(
                () => VaultSessionManager.CreateAsync(vaultDirectory));

            Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
            Assert.True(File.Exists(userFile));
        }
        finally
        {
            Directory.Delete(vaultDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptEnvelopeIsRejectedAndPreserved()
    {
        var vaultDirectory = CreateTemporaryDirectory();
        var keyPath = Path.Combine(vaultDirectory, "vault.key");
        var databasePath = Path.Combine(vaultDirectory, "vault.db");
        File.WriteAllText(keyPath, "{}");
        File.WriteAllBytes(databasePath, [1, 2, 3]);
        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(
                () => VaultSessionManager.CreateAsync(vaultDirectory));

            Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
            Assert.True(File.Exists(keyPath));
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            Directory.Delete(vaultDirectory, recursive: true);
        }
    }

    private static async Task CreateInitializedVaultAsync(string vaultDirectory)
    {
        var creation = VaultKeyService.Create(
            "correct horse battery staple",
            Guid.NewGuid());
        try
        {
            await VaultKeyEnvelopeSerializer.WriteAtomicAsync(
                Path.Combine(vaultDirectory, "vault.key"),
                creation.Envelope);
            await SqliteMcVaultStore.InitializeAsync(
                vaultDirectory,
                creation.KeyMaterial);
        }
        finally
        {
            creation.KeyMaterial.Dispose();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-manager-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
