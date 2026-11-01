using CipherVault.Application.Vault;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Tests;

public sealed class ServerProcessResourcesTests
{
    [Fact]
    public async Task DisposesManagerAndCallsSqliteShutdownOnce()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-resources-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var shutdownCalls = 0;
        try
        {
            var manager = await VaultSessionManager.CreateAsync(root);
            _ = await manager.InitializeAsync(
                ServerTestApplication.Password);
            _ = await manager.UnlockAsync(
                ServerTestApplication.Password);
            var resources = new ServerProcessResources(() =>
            {
                shutdownCalls++;
                return ValueTask.CompletedTask;
            });
            resources.AttachManager(manager);
            resources.MarkSqliteTouched();

            await resources.DisposeAsync();
            await resources.DisposeAsync();

            Assert.Equal(1, shutdownCalls);
            await using var restarted =
                await VaultSessionManager.CreateAsync(root);
            Assert.Equal(VaultSessionState.Locked, restarted.State);
            _ = await restarted.UnlockAsync(
                ServerTestApplication.Password);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ShutdownOnlyRunsWhenSqliteWasTouched()
    {
        var shutdownCalls = 0;
        var resources = new ServerProcessResources(() =>
        {
            shutdownCalls++;
            return ValueTask.CompletedTask;
        });

        await resources.DisposeAsync();

        Assert.Equal(0, shutdownCalls);
    }
}
