using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultDatabaseRuntimeDisposeTests
{
    [Fact]
    public async Task DisposeIsIdempotentAndDoesNotShutdownProcess()
    {
        await using var fixture = await Fixture.CreateAsync();
        var shutdownCount = SqliteMcDatabaseFactory.ShutdownCallCount;

        await fixture.Runtime.DisposeAsync();
        await fixture.Runtime.DisposeAsync();

        Assert.Equal(shutdownCount, SqliteMcDatabaseFactory.ShutdownCallCount);
        Assert.Equal(32, fixture.Material.DatabaseKey.Length);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await fixture.Runtime.ExecuteAsync(
                (_, _) => ValueTask.FromResult(1)));
    }

    [Fact]
    public async Task DisposeRejectsAnActiveOperationWithoutClosingConnection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(async () =>
            await fixture.Runtime.ExecuteAsync<int>(async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return 1;
            }));

        await started.Task;
        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await fixture.Runtime.DisposeAsync());
        Assert.Equal(VaultErrorCode.VaultBusy, exception.Code);

        release.SetResult();
        Assert.Equal(1, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisposeAfterCancelledCallbackReleasesDatabaseFile()
    {
        var fixture = await Fixture.CreateAsync();
        var root = fixture.RootPath;
        var runtime = fixture.Runtime;
        var material = fixture.Material;
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var operation = Task.Run(async () =>
            await runtime.ExecuteAsync<int>(
                async (_, _) =>
                {
                    started.SetResult();
                    await release.Task;
                    return 1;
                },
                cancellation.Token));
        await started.Task;
        cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await operation.WaitAsync(TimeSpan.FromSeconds(5)));

        await runtime.DisposeAsync();
        material.Dispose();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(50);
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            VaultKeyMaterial material,
            VaultDatabaseRuntime runtime)
        {
            Root = root;
            Material = material;
            Runtime = runtime;
        }

        internal VaultDatabaseRuntime Runtime { get; }

        internal VaultKeyMaterial Material { get; }

        private string Root { get; }

        internal string RootPath => Root;

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-dispose-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var material = new VaultKeyMaterial(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32));
            try
            {
                await SqliteMcVaultStore.InitializeAsync(root, material);
                var runtime = await VaultDatabaseRuntime.OpenAsync(
                    root,
                    material,
                    Guid.NewGuid(),
                    generation: 1);
                return new Fixture(root, material, runtime);
            }
            catch
            {
                material.Dispose();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Runtime.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            Material.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
