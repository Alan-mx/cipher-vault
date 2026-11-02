using CipherVault.Application.Backup;
using CipherVault.Core.Backup;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class BackupStorageTests
{
    [Fact]
    public async Task DirectoryLockRejectsConcurrentOwner()
    {
        var root = CreateRoot();
        var lockPath = Path.Combine(root, "backup.lock");
        try
        {
            await using var first = BackupDirectoryLock.Acquire(lockPath);
            var exception = Assert.Throws<VaultException>(
                () => BackupDirectoryLock.Acquire(lockPath));
            Assert.Equal(VaultErrorCode.BackupInProgress, exception.Code);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StartupCleanupRemovesOnlyStrictPartialFiles()
    {
        var root = CreateRoot();
        try
        {
            var stale = Path.Combine(
                root,
                $"{BackupConstants.BackupTempPrefix}{Guid.NewGuid():N}{BackupConstants.PartialSuffix}");
            var unrelated = Path.Combine(root, "keep.txt");
            await File.WriteAllTextAsync(stale, "partial");
            await File.WriteAllTextAsync(unrelated, "keep");
            var service = new BackupStartupCleanupService(
                new BackupStorageOptions(
                    root,
                    Path.Combine(root, BackupConstants.BackupLockFileName)));

            await service.InitializeAsync();

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(
                Path.Combine(root, BackupConstants.BackupLockFileName)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-backup-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
