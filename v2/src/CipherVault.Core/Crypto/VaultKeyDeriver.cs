using System.Text;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Crypto;

public static class VaultKeyDeriver
{
    public const string DatabaseKeyInfo = "cipher-vault/database-key/v1";

    public static byte[] DeriveDatabaseKey(Guid vaultId, ReadOnlySpan<byte> masterKey)
    {
        if (vaultId == Guid.Empty)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Vault ID must not be empty.");
        }

        if (masterKey.Length != MasterKeyProtector.MasterKeyBytes)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "MasterKey must be exactly 32 bytes.");
        }

        var salt = Encoding.UTF8.GetBytes(vaultId.ToString("D").ToLowerInvariant());
        var info = Encoding.UTF8.GetBytes(DatabaseKeyInfo);
        var masterKeyCopy = masterKey.ToArray();
        try
        {
            return System.Security.Cryptography.HKDF.DeriveKey(
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                masterKeyCopy,
                32,
                salt,
                info);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKeyCopy);
        }
    }
}
