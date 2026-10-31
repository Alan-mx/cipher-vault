using CipherVault.Server.Hosting;

namespace CipherVault.Server.Tests;

public sealed class ServerInstanceLockTests
{
    [Fact]
    public async Task SecondProcessLockIsRejected()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var lockPath = Path.Combine(parent, "server.lock");
        try
        {
            await using var first = ServerInstanceLock.Acquire(lockPath);
            var exception = Assert.Throws<ServerStartupException>(
                () => ServerInstanceLock.Acquire(lockPath));
            Assert.Equal(ServerStartupCode.InstanceConflict, exception.Code);
            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
