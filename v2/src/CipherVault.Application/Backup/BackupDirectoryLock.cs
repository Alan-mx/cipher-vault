using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Application.Backup;

internal sealed class BackupDirectoryLock : IAsyncDisposable
{
    private readonly FileStream stream;
    private int disposed;

    private BackupDirectoryLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static BackupDirectoryLock Acquire(string lockPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(lockPath)
                ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Backup lock path must have a parent directory.");
            Directory.CreateDirectory(directory);
            VaultFilePermissions.ProtectDirectory(directory);
            PathSafety.EnsureNoLinkComponents(lockPath);
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            try
            {
                VaultFilePermissions.ProtectFile(lockPath);
                return new BackupDirectoryLock(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
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
                VaultErrorCode.BackupInProgress,
                "Another backup operation owns the backup directory.",
                exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            stream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
