using System.Text;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Security;

public static class AtomicFileWriter
{
    public static async Task WriteAsync(
        string targetPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var tempPath = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(targetPath)
                ?? throw new VaultException(VaultErrorCode.InvalidInput, "Target path has no parent directory.");
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(
                directory,
                $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                VaultFilePermissions.ProtectFile(tempPath);
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(targetPath))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.Replace(tempPath, targetPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
            tempPath = string.Empty;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to write the Vault file atomically.");
        }
        finally
        {
            if (tempPath.Length > 0 && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException)
                {
                    // Preserve the original operation result; an orphaned temp file is not user data.
                }
            }
        }
    }

    public static Task WriteAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken = default) =>
        WriteAsync(targetPath, Encoding.UTF8.GetBytes(content), cancellationToken);
}
