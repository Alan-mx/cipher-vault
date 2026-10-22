using System.Security.Principal;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Security;

public sealed class MachineExclusiveFileLock : IDisposable, IAsyncDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private MachineExclusiveFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static MachineExclusiveFileLock Acquire(string lockPath)
    {
        var fullPath = Path.GetFullPath(lockPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Machine lock path has no parent directory.");
        try
        {
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsWindows())
            {
                var currentUser = WindowsIdentity.GetCurrent().User
                    ?? throw new VaultException(
                        VaultErrorCode.StorageError,
                        "Unable to resolve the current Windows user.");
                VaultFileSecurity.ProtectDirectoryForSids(
                    directory,
                    [currentUser]);
            }
            var stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return new MachineExclusiveFileLock(stream);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new VaultException(
                VaultErrorCode.Conflict,
                "Another CipherVault process already owns this machine lock.",
                exception);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Machine lock could not be acquired.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}
