using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Database;

public static class SqliteMcVaultStore
{
    public static async Task<VaultDatabaseInfo> InitializeAsync(
        string vaultDirectory,
        VaultKeyMaterial material,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(material);
            await using var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
                vaultDirectory,
                material,
                create: true,
                cancellationToken);
            return session.DatabaseInfo;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault database initialization failed.");
        }
    }

    public static async Task<VaultDatabaseInfo> VerifyAsync(
        string vaultDirectory,
        VaultKeyMaterial material,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(material);
            await using var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
                vaultDirectory,
                material,
                create: false,
                cancellationToken);
            return session.DatabaseInfo;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault database verification failed.");
        }
    }
}
