using CipherVault.Core.Errors;

namespace CipherVault.Core.Crypto;

public sealed record VaultKeyWrap(
    string Algorithm,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag)
{
    public void Validate()
    {
        if (!string.Equals(Algorithm, "aes-256-gcm", StringComparison.Ordinal))
        {
            throw Corrupt("Unsupported key-wrap algorithm.");
        }

        if (Nonce is null || Nonce.Length != 12)
        {
            throw Corrupt("AES-GCM Nonce must be exactly 12 bytes.");
        }

        if (Ciphertext is null || Ciphertext.Length != 32)
        {
            throw Corrupt("Wrapped MasterKey ciphertext must be exactly 32 bytes.");
        }

        if (Tag is null || Tag.Length != 16)
        {
            throw Corrupt("AES-GCM authentication tag must be exactly 16 bytes.");
        }
    }

    private static VaultException Corrupt(string message) =>
        new(VaultErrorCode.CorruptVault, message);
}

public sealed record VaultKeyEnvelope(
    string Format,
    int Version,
    Guid VaultId,
    VaultKdfParameters Kdf,
    VaultKeyWrap KeyWrap)
{
    public const string ExpectedFormat = "cipher-vault-key-envelope";
    public const int CurrentVersion = 1;

    public void Validate()
    {
        if (!string.Equals(Format, ExpectedFormat, StringComparison.Ordinal))
        {
            throw Corrupt("Unsupported key-envelope format.");
        }

        if (Version != CurrentVersion)
        {
            throw Corrupt("Unsupported key-envelope version.");
        }

        if (VaultId == Guid.Empty)
        {
            throw Corrupt("Vault ID must not be empty.");
        }

        if (Kdf is null)
        {
            throw Corrupt("KDF parameters are required.");
        }

        Kdf.Validate();

        if (KeyWrap is null)
        {
            throw Corrupt("Key-wrap data is required.");
        }

        KeyWrap.Validate();
    }

    private static VaultException Corrupt(string message) =>
        new(VaultErrorCode.CorruptVault, message);
}
