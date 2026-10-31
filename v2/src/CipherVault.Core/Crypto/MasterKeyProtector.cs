using System.Security.Cryptography;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Crypto;

public static class MasterKeyProtector
{
    public const int MasterKeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public static VaultKeyWrap Wrap(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> kek,
        Guid vaultId,
        VaultKdfParameters parameters)
    {
        if (masterKey.Length != MasterKeyBytes)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "MasterKey must be exactly 32 bytes.");
        }

        if (kek.Length != MasterKeyBytes)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "KEK must be exactly 32 bytes.");
        }

        parameters.Validate();
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[MasterKeyBytes];
        var tag = new byte[TagBytes];
        var aad = VaultEnvelopeAad.Build(vaultId, parameters);
        using var aes = new AesGcm(kek, TagBytes);
        aes.Encrypt(nonce, masterKey, ciphertext, tag, aad);

        return new VaultKeyWrap("aes-256-gcm", nonce, ciphertext, tag);
    }

    public static byte[] Unwrap(
        VaultKeyEnvelope envelope,
        ReadOnlySpan<byte> kek)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Validate();
        if (kek.Length != MasterKeyBytes)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "KEK must be exactly 32 bytes.");
        }

        var masterKey = new byte[MasterKeyBytes];
        var aad = VaultEnvelopeAad.Build(envelope.VaultId, envelope.Kdf);
        try
        {
            using var aes = new AesGcm(kek, TagBytes);
            aes.Decrypt(
                envelope.KeyWrap.Nonce,
                envelope.KeyWrap.Ciphertext,
                envelope.KeyWrap.Tag,
                masterKey,
                aad);
            return masterKey;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new VaultException(
                VaultErrorCode.InvalidCredential,
                "Vault credential is invalid.",
                exception);
        }
    }
}
