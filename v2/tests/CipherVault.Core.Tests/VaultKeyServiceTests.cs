using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultKeyServiceTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task CorrectPasswordReturnsSameKeys()
    {
        var creation = VaultKeyService.Create(Password, Guid.NewGuid());
        var path = await WriteEnvelopeAsync(creation.Envelope);
        try
        {
            await using var unlocked = await VaultKeyService.UnlockAsync(path, Password);

            Assert.Equal(creation.KeyMaterial.VaultId, unlocked.VaultId);
            Assert.True(creation.KeyMaterial.MasterKey.Span.SequenceEqual(unlocked.MasterKey.Span));
            Assert.True(creation.KeyMaterial.DatabaseKey.Span.SequenceEqual(unlocked.DatabaseKey.Span));
        }
        finally
        {
            creation.KeyMaterial.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WrongPasswordReturnsStableInvalidCredential()
    {
        var creation = VaultKeyService.Create(Password, Guid.NewGuid());
        var path = await WriteEnvelopeAsync(creation.Envelope);
        creation.KeyMaterial.Dispose();

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                VaultKeyService.UnlockAsync(path, "wrong password is still long enough"));

            Assert.Equal(VaultErrorCode.InvalidCredential, exception.Code);
            Assert.DoesNotContain(Password, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ShortAuthenticationCandidateReturnsInvalidCredential()
    {
        var creation = VaultKeyService.Create(Password, Guid.NewGuid());
        var path = await WriteEnvelopeAsync(creation.Envelope);
        creation.KeyMaterial.Dispose();

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                VaultKeyService.UnlockAsync(path, "short"));
            Assert.Equal(VaultErrorCode.InvalidCredential, exception.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TamperedEnvelopeFailsAuthentication()
    {
        var creation = VaultKeyService.Create(Password, Guid.NewGuid());
        var envelope = creation.Envelope with
        {
            KeyWrap = creation.Envelope.KeyWrap with
            {
                Tag = creation.Envelope.KeyWrap.Tag.ToArray()
            }
        };
        envelope.KeyWrap.Tag[0] ^= 0x33;
        creation.KeyMaterial.Dispose();
        var path = await WriteEnvelopeAsync(envelope);

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                VaultKeyService.UnlockAsync(path, Password));

            Assert.Equal(VaultErrorCode.InvalidCredential, exception.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposedMaterialRejectsKeyAccess()
    {
        var creation = VaultKeyService.Create(Password, Guid.NewGuid());
        var path = await WriteEnvelopeAsync(creation.Envelope);
        creation.KeyMaterial.Dispose();

        try
        {
            await using var unlocked = await VaultKeyService.UnlockAsync(path, Password);
            Assert.Equal(32, unlocked.DatabaseKey.Length);
            await unlocked.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => _ = unlocked.MasterKey);
            Assert.True(unlocked.IsClearedForTest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DatabaseKeyDerivationIsDeterministic()
    {
        var vaultId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var masterKey = RandomNumberGenerator.GetBytes(32);

        try
        {
            var first = VaultKeyDeriver.DeriveDatabaseKey(vaultId, masterKey);
            var second = VaultKeyDeriver.DeriveDatabaseKey(vaultId, masterKey);

            try
            {
                Assert.Equal(32, first.Length);
                Assert.Equal(first, second);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(first);
                CryptographicOperations.ZeroMemory(second);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static async Task<string> WriteEnvelopeAsync(VaultKeyEnvelope envelope)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cipher-vault-key-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, VaultKeyEnvelopeSerializer.Serialize(envelope));
        return path;
    }
}
