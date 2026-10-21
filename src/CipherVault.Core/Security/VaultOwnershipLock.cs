using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Security;

public sealed class VaultOwnershipLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private VaultOwnershipLock(FileStream stream)
    {
        _stream = stream;
    }

    public static Task<VaultOwnershipLock> AcquireAsync(
        string vaultPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(vaultPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..32];
        var lockDirectory = Path.Combine(
            Path.GetTempPath(),
            "CipherVault",
            "locks");
        var lockPath = Path.Combine(lockDirectory, $"{hash}.owner.lock");

        try
        {
            Directory.CreateDirectory(lockDirectory);
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return Task.FromResult(new VaultOwnershipLock(stream));
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new VaultException(
                VaultErrorCode.Conflict,
                "Vault is already in use by another process.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Vault ownership lock could not be acquired.",
                exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}
