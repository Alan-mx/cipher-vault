namespace CipherVault.Application.Vault;

internal enum VaultInstanceLayout
{
    Absent,
    Initialized,
    Partial,
    InvalidExistingDirectory,
    CorruptEnvelope
}
