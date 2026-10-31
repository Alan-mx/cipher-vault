using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Tests;

public sealed class PasswordChangeTests
{
    private const string OldPassword = "correct horse battery staple";
    private const string NewPassword = "new correct horse battery staple";

    [Fact]
    public async Task SuccessfulChangeKeepsMasterAndDatabaseKey()
    {
        var creation = VaultKeyService.Create(OldPassword, Guid.NewGuid());
        var path = await WriteEnvelopeAsync(creation.Envelope);
        try
        {
            var oldEnvelope = creation.Envelope;
            var change = await VaultKeyService.ChangePasswordAsync(path, OldPassword, NewPassword);
            try
            {
                Assert.NotEqual(oldEnvelope.Kdf.Salt, change.Envelope.Kdf.Salt);
                Assert.NotEqual(oldEnvelope.KeyWrap.Nonce, change.Envelope.KeyWrap.Nonce);
                Assert.True(creation.KeyMaterial.MasterKey.Span.SequenceEqual(change.KeyMaterial.MasterKey.Span));
                Assert.True(creation.KeyMaterial.DatabaseKey.Span.SequenceEqual(change.KeyMaterial.DatabaseKey.Span));

                await Assert.ThrowsAsync<VaultException>(() =>
                    VaultKeyService.UnlockAsync(path, OldPassword));

                await using var newUnlock = await VaultKeyService.UnlockAsync(path, NewPassword);
                Assert.True(creation.KeyMaterial.DatabaseKey.Span.SequenceEqual(newUnlock.DatabaseKey.Span));
            }
            finally
            {
                change.KeyMaterial.Dispose();
            }
        }
        finally
        {
            creation.KeyMaterial.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteFailureKeepsOldEnvelope()
    {
        var creation = VaultKeyService.Create(OldPassword, Guid.NewGuid());
        var path = await WriteEnvelopeAsync(creation.Envelope);
        var originalBytes = await File.ReadAllBytesAsync(path);

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                VaultKeyService.ChangePasswordCoreAsync(
                    path,
                    OldPassword,
                    NewPassword,
                    (_, _) => throw new IOException("simulated write failure"),
                    CancellationToken.None));
            Assert.Equal(VaultErrorCode.StorageError, exception.Code);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));

            await using var oldUnlock = await VaultKeyService.UnlockAsync(path, OldPassword);
            Assert.Equal(creation.KeyMaterial.DatabaseKey.ToArray(), oldUnlock.DatabaseKey.ToArray());
        }
        finally
        {
            creation.KeyMaterial.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AtomicWriterReplacesContentWithoutTemporaryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "vault.key");
        try
        {
            await AtomicFileWriter.WriteAsync(path, "first");
            await AtomicFileWriter.WriteAsync(path, "second");

            Assert.Equal("second", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> WriteEnvelopeAsync(VaultKeyEnvelope envelope)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cipher-vault-key-{Guid.NewGuid():N}.json");
        await VaultKeyEnvelopeSerializer.WriteAtomicAsync(path, envelope);
        return path;
    }
}
