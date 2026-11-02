using CipherVault.Core.Backup;

namespace CipherVault.Core.Tests;

public sealed class RegularFileIdentityTests
{
    [Fact]
    public async Task PlatformIdentityDetectsRewriteAfterTimestampRestore()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-file-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "backup.cvbackup");
        try
        {
            await File.WriteAllTextAsync(path, "AAAA");
            var created = File.GetCreationTimeUtc(path);
            var modified = File.GetLastWriteTimeUtc(path);
            var first = RegularFileIdentity.Read(path);
            var repeated = RegularFileIdentity.Read(path);

            Assert.Equal(first, repeated);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                Assert.True(first.IsReliable);
            }

            await File.WriteAllTextAsync(path, "BBBB");
            File.SetCreationTimeUtc(path, created);
            File.SetLastWriteTimeUtc(path, modified);
            var rewritten = RegularFileIdentity.Read(path);

            if (first.IsReliable)
            {
                Assert.NotEqual(first, rewritten);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
