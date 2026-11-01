using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Backup;

public static class BackupDatabaseStore
{
    public static async Task CreateEncryptedSnapshotAsync(
        VaultOperationContext context,
        string workingDirectory,
        ReadOnlyMemory<byte> databaseKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var directory = PathSafety.Canonicalize(
            workingDirectory,
            "Backup working directory");
        Directory.CreateDirectory(directory);
        VaultFilePermissions.ProtectDirectory(directory);
        PathSafety.EnsureNoLinkComponents(directory);
        var databasePath = Path.Combine(
            directory,
            BackupConstants.DatabaseEntryName);
        if (File.Exists(databasePath))
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Backup snapshot destination already exists.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var destination =
                await SqliteMcDatabaseFactory.OpenConnectionAsync(
                    new DatabaseOpenRequest(
                        directory,
                        databaseKey,
                        CreateIfMissing: true),
                    cancellationToken);
            context.Connection.BackupDatabase(destination);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Unable to create the encrypted backup snapshot.",
                exception);
        }

        DeleteDatabaseSidecars(directory);
        var info = new FileInfo(databasePath);
        if (!info.Exists || info.Length <= 0)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Encrypted backup snapshot was not created.");
        }
    }

    internal static void DeleteDatabaseSidecars(string directory)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var path = Path.Combine(
                directory,
                BackupConstants.DatabaseEntryName + suffix);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
