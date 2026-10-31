using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Server.Hosting;

public sealed class ServerInstanceLock : IAsyncDisposable
{
    private readonly FileStream stream;
    private int disposed;

    private ServerInstanceLock(string lockPath, FileStream stream)
    {
        LockPath = lockPath;
        this.stream = stream;
    }

    public string LockPath { get; }

    public static ServerInstanceLock Acquire(string lockPath)
    {
        try
        {
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
                return new ServerInstanceLock(lockPath, stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException exception)
        {
            throw new ServerStartupException(
                ServerStartupCode.InstanceConflict,
                "Another CipherVault.Server instance owns the lock.",
                exception);
        }
        catch (VaultException exception)
        {
            throw new ServerStartupException(
                ServerStartupCode.StorageFailure,
                "Unable to protect the CipherVault server lock.",
                exception);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new ServerStartupException(
                ServerStartupCode.StorageFailure,
                "Unable to acquire the CipherVault server lock.",
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
