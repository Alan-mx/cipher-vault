using CipherVault.Core.Errors;

namespace CipherVault.Application.Vault;

internal sealed record VaultInstancePaths
{
    internal const string KeyFileName = "vault.key";
    internal const string DatabaseFileName = "vault.db";

    private VaultInstancePaths(string vaultDirectory)
    {
        VaultDirectory = vaultDirectory;
        KeyPath = Path.Combine(vaultDirectory, KeyFileName);
        DatabasePath = Path.Combine(vaultDirectory, DatabaseFileName);
    }

    internal string VaultDirectory { get; }

    internal string KeyPath { get; }

    internal string DatabasePath { get; }

    internal static VaultInstancePaths Create(string vaultDirectory)
    {
        if (string.IsNullOrWhiteSpace(vaultDirectory))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Vault directory is required.");
        }

        try
        {
            return new VaultInstancePaths(Path.GetFullPath(vaultDirectory));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Vault directory is invalid.");
        }
    }
}
