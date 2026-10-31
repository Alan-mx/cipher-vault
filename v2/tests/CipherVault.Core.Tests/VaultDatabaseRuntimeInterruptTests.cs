using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultDatabaseRuntimeInterruptTests
{
    [Fact]
    public async Task InterruptCancelsARealLongRunningSqliteCommand()
    {
        await using var fixture = await Fixture.CreateAsync();
        var commandStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = Task.Run(async () =>
            await fixture.Runtime.ExecuteAsync<int>(
                async (context, cancellationToken) =>
                {
                    using var lease = context.CreateCommand();
                    lease.Command.CommandText =
                        """
                        WITH RECURSIVE counter(value) AS (
                            VALUES(0)
                            UNION ALL
                            SELECT value + 1 FROM counter WHERE value < 1000000000
                        )
                        SELECT sum(value) FROM counter;
                        """;
                    commandStarted.SetResult();
                    var result = await lease.Command.ExecuteScalarAsync(cancellationToken);
                    return Convert.ToInt32(result);
                }));

        await commandStarted.Task;
        await Task.Delay(100);
        Assert.True(fixture.Runtime.TryInterruptActiveCommand());

        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await operation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(VaultErrorCode.DatabaseError, exception.Code);
        Assert.False(fixture.Runtime.TryInterruptActiveCommand());
    }

    [Fact]
    public async Task InterruptReturnsFalseWhenNoCommandIsActive()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.False(fixture.Runtime.TryInterruptActiveCommand());
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

        private string Root { get; }

        private VaultKeyMaterial Material { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-interrupt-{Guid.NewGuid():N}");
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
            await Runtime.DisposeAsync();
            Material.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
