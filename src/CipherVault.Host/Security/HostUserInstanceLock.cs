using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CipherVault.Core.Errors;

namespace CipherVault.Host.Security;

public sealed class HostUserInstanceLock : IDisposable
{
    private FileStream? _stream;

    private HostUserInstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static HostUserInstanceLock Acquire(string? scopeId = null)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value
            ?? Environment.UserName;
        var suffix = string.IsNullOrWhiteSpace(scopeId)
            ? user
            : $"{user}.{scopeId}";
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(suffix.ToUpperInvariant())))[..32];
        var lockDirectory = Path.Combine(
            Path.GetTempPath(),
            "CipherVault",
            "host-instance");
        var lockPath = Path.Combine(lockDirectory, $"{hash}.lock");
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
            return new HostUserInstanceLock(stream);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new VaultException(
                VaultErrorCode.Conflict,
                "Another CipherVault Host is already running for this Windows user.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "CipherVault Host instance lock could not be acquired.",
                exception);
        }
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }
        stream.Dispose();
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}
