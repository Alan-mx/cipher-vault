using System.Text.RegularExpressions;
using CipherVault.Core.Backup;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Application.Backup;

public sealed class BackupStartupCleanupService
{
    private static readonly Regex PartialName = new(
        "^\\.cipher-vault-backup-[0-9a-f]{32}\\.partial$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly BackupStorageOptions options;

    public BackupStartupCleanupService(BackupStorageOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.EnsureNoLinkComponents(options.BackupDirectory);
        Directory.CreateDirectory(options.BackupDirectory);
        VaultFilePermissions.ProtectDirectory(options.BackupDirectory);
        await using var directoryLock = BackupDirectoryLock.Acquire(
            options.BackupLockPath);
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     options.BackupDirectory,
                     $"{BackupConstants.BackupTempPrefix}*{BackupConstants.PartialSuffix}",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PartialName.IsMatch(Path.GetFileName(path)))
            {
                continue;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Unsafe backup partial artifact was found.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                EnsureNoLinks(path);
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private static void EnsureNoLinks(string directory)
    {
        PathSafety.EnsureNoLinkComponents(directory);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new VaultException(
                        VaultErrorCode.StorageError,
                        "Unsafe backup partial artifact was found.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
            }
        }
    }
}
