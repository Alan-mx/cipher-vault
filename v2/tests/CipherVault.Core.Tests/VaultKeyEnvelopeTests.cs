using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultKeyEnvelopeTests
{
    [Fact]
    public void WrapAndUnwrapRoundTrip()
    {
        var vaultId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var parameters = VaultKdfParameters.CreateDefault(Encoding.UTF8.GetBytes("0123456789abcdef"));
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var kek = RandomNumberGenerator.GetBytes(32);

        var wrap = MasterKeyProtector.Wrap(masterKey, kek, vaultId, parameters);
        var envelope = new VaultKeyEnvelope(
            VaultKeyEnvelope.ExpectedFormat,
            VaultKeyEnvelope.CurrentVersion,
            vaultId,
            parameters,
            wrap);
        var unwrapped = MasterKeyProtector.Unwrap(envelope, kek);

        try
        {
            Assert.Equal(masterKey, unwrapped);
            Assert.Equal(12, wrap.Nonce.Length);
            Assert.Equal(32, wrap.Ciphertext.Length);
            Assert.Equal(16, wrap.Tag.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(unwrapped);
        }
    }

    [Fact]
    public void WrongKekAndTamperingFailAuthentication()
    {
        var envelope = CreateEnvelope();
        var wrongKek = RandomNumberGenerator.GetBytes(32);

        try
        {
            Assert.Equal(
                VaultErrorCode.InvalidCredential,
                Assert.Throws<VaultException>(() => MasterKeyProtector.Unwrap(envelope, wrongKek)).Code);

            var tampered = envelope with
            {
                KeyWrap = envelope.KeyWrap with
                {
                    Tag = envelope.KeyWrap.Tag.ToArray()
                }
            };
            tampered.KeyWrap.Tag[0] ^= 0x5A;

            Assert.Equal(
                VaultErrorCode.InvalidCredential,
                Assert.Throws<VaultException>(() => MasterKeyProtector.Unwrap(tampered, Kek)).Code);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrongKek);
        }
    }

    [Fact]
    public async Task JsonUsesCamelCaseAndRoundTrips()
    {
        var envelope = CreateEnvelope();
        var path = Path.Combine(Path.GetTempPath(), $"cipher-vault-envelope-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, VaultKeyEnvelopeSerializer.Serialize(envelope));
        var json = await File.ReadAllTextAsync(path);

        try
        {
            Assert.Contains("\"format\"", json, StringComparison.Ordinal);
            Assert.Contains("\"vaultId\"", json, StringComparison.Ordinal);
            Assert.Contains("\"keyWrap\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("recovery", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(masterKeyMarker(envelope), json, StringComparison.Ordinal);

            var loaded = await VaultKeyEnvelopeSerializer.LoadAsync(path);
            Assert.Equal(envelope.VaultId, loaded.VaultId);
            Assert.Equal(envelope.Kdf.Salt, loaded.Kdf.Salt);
            Assert.Equal(envelope.KeyWrap.Ciphertext, loaded.KeyWrap.Ciphertext);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnknownJsonPropertyIsRejected()
    {
        var path = WriteJson(
            """
            {
              "format":"cipher-vault-key-envelope",
              "version":1,
              "vaultId":"11111111-2222-3333-4444-555555555555",
              "recovery":{},
              "kdf":{
                "algorithm":"argon2id",
                "memoryKiB":65536,
                "iterations":3,
                "parallelism":1,
                "salt":"MDEyMzQ1Njc4OWFiY2RlZg=="
              },
              "keyWrap":{
                "algorithm":"aes-256-gcm",
                "nonce":"AAAAAAAAAAAAAAAA",
                "ciphertext":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                "tag":"AAAAAAAAAAAAAAAAAAAAAA=="
              }
            }
            """);

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                VaultKeyEnvelopeSerializer.LoadAsync(path));
            Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OversizedEnvelopeIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cipher-vault-envelope-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, new byte[VaultKeyEnvelopeSerializer.MaxEnvelopeBytes + 1]);

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                VaultKeyEnvelopeSerializer.LoadAsync(path));
            Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AadUsesCanonicalVaultAndKdfValues()
    {
        var parameters = VaultKdfParameters.CreateDefault(Encoding.UTF8.GetBytes("0123456789abcdef"));
        var aad = Encoding.UTF8.GetString(
            VaultEnvelopeAad.Build(Guid.Parse("11111111-2222-3333-4444-555555555555"), parameters));

        Assert.Equal(
            "cipher-vault-key-envelope-v1|11111111-2222-3333-4444-555555555555|argon2id|65536|3|1",
            aad);
    }

    private static VaultKeyEnvelope CreateEnvelope()
    {
        var vaultId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var parameters = VaultKdfParameters.CreateDefault(Encoding.UTF8.GetBytes("0123456789abcdef"));
        return new VaultKeyEnvelope(
            VaultKeyEnvelope.ExpectedFormat,
            VaultKeyEnvelope.CurrentVersion,
            vaultId,
            parameters,
            MasterKeyProtector.Wrap(
                Encoding.UTF8.GetBytes("01234567890123456789012345678901"),
                Kek,
                vaultId,
                parameters));
    }

    private static readonly byte[] Kek = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyz123456");

    private static string masterKeyMarker(VaultKeyEnvelope envelope) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("01234567890123456789012345678901"));

    private static string WriteJson(string value)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cipher-vault-envelope-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, value, new UTF8Encoding(false));
        return path;
    }
}
