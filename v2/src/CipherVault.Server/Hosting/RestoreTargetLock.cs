using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Server.Hosting;

internal sealed class RestoreTargetLock : IAsyncDisposable
{
    private readonly FileStream stream;
    private int disposed;

    private RestoreTargetLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static string GetTargetHash(string targetPath)
    {
        var canonical = PathSafety.Canonicalize(
            targetPath,
            "Restore target");
        var comparable = OperatingSystem.IsWindows()
            ? canonical.ToUpperInvariant()
            : canonical;
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(comparable)))
            .ToLowerInvariant();
    }

    public static RestoreTargetLock Acquire(
        string targetPath)
    {
        var target = PathSafety.Canonicalize(
            targetPath,
            "Restore target");
        var parent = Path.GetDirectoryName(target)
            ?? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Restore target has no parent directory.");
        var lockPath = Path.Combine(
            parent,
            $".cipher-vault-restore-{GetTargetHash(target)}.lock");
        try
        {
            PathSafety.EnsureNoLinkComponents(lockPath);
            if (!File.Exists(lockPath))
            {
                using (new FileStream(
                           lockPath,
                           FileMode.CreateNew,
                           FileAccess.ReadWrite,
                           FileShare.None))
                {
                }
            }
            VaultFilePermissions.ProtectFile(lockPath);
            var stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new RestoreTargetLock(stream);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Restore target is already in use or unavailable.",
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
