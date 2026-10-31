using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultDatabaseRuntimeTests
{
    [Fact]
    public async Task OpenAsyncReturnsRuntimeWithoutTakingMaterialOwnership()
    {
        var root = CreateTemporaryDirectory();
        var vaultId = Guid.NewGuid();
        using var material = CreateMaterial(vaultId);
        var sessionId = Guid.NewGuid();

        try
        {
            await SqliteMcVaultStore.InitializeAsync(root, material);
            var runtime = await VaultDatabaseRuntime.OpenAsync(
                root,
                material,
                sessionId,
                generation: 1);

            Assert.Equal(vaultId, runtime.VaultId);
            Assert.Equal(sessionId, runtime.SessionId);
            Assert.Equal(1, runtime.Generation);
            Assert.Equal(32, material.DatabaseKey.Length);

            await runtime.DisposeAsync();
            Assert.Equal(32, material.DatabaseKey.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsyncRejectsWrongDatabaseKey()
    {
        var root = CreateTemporaryDirectory();
        var vaultId = Guid.NewGuid();
        using var correct = CreateMaterial(vaultId);
        using var wrong = CreateMaterial(vaultId);

        try
        {
            await SqliteMcVaultStore.InitializeAsync(root, correct);

            await Assert.ThrowsAsync<VaultException>(async () =>
                await VaultDatabaseRuntime.OpenAsync(
                    root,
                    wrong,
                    Guid.NewGuid(),
                    generation: 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task OpenAsyncRejectsInvalidSessionIdentity(
        bool emptySessionId,
        long generation)
    {
        var root = CreateTemporaryDirectory();
        using var material = CreateMaterial(Guid.NewGuid());
        try
        {
            await Assert.ThrowsAsync<VaultException>(async () =>
                await VaultDatabaseRuntime.OpenAsync(
                    root,
                    material,
                    emptySessionId ? Guid.Empty : Guid.NewGuid(),
                    generation));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static VaultKeyMaterial CreateMaterial(Guid vaultId) =>
        new(
            vaultId,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
