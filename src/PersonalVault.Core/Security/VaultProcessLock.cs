using System.Security.Cryptography;
using System.Text;

namespace PersonalVault.Core.Security;

internal sealed class VaultProcessLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _held = true;

    private VaultProcessLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<VaultProcessLock> AcquireAsync(
        string vaultPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(vaultPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..32];
        var lockDirectory = Path.Combine(
            Path.GetTempPath(),
            "PersonalVault",
            "locks");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"{hash}.lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
                return new VaultProcessLock(stream);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    public void Dispose()
    {
        if (!_held)
        {
            return;
        }

        _held = false;
        _stream.Dispose();
    }
}
