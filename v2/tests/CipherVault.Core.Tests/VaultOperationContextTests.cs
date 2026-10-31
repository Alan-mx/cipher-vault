using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultOperationContextTests
{
    [Fact]
    public async Task CommitIsDeferredUntilFinalize()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var key = $"tx-{Guid.NewGuid():N}";
        var context = fixture.Runtime.CreateOperationContext();
        await context.BeginTransactionAsync();
        using (var lease = context.CreateCommand())
        {
            lease.Command.CommandText =
                "INSERT INTO vault_meta(key, value) VALUES ($key, 'committed');";
            lease.Command.Parameters.AddWithValue("$key", key);
            await lease.Command.ExecuteNonQueryAsync();
        }

        await context.CommitTransactionAsync();

        var beforeFinalize = await fixture.ReadCommittedValueAsync(key);
        Assert.Null(beforeFinalize);

        await context.FinalizeTransactionAsync(
            operationSucceeded: true,
            CancellationToken.None);
        var afterFinalize = await fixture.ReadCommittedValueAsync(key);
        Assert.Equal("committed", afterFinalize);

        await context.DisposeAsync();
    }

    [Fact]
    public async Task DisposingUncommittedContextRollsBack()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var key = $"tx-{Guid.NewGuid():N}";
        var context = fixture.Runtime.CreateOperationContext();
        await context.BeginTransactionAsync();
        using (var lease = context.CreateCommand())
        {
            lease.Command.CommandText =
                "INSERT INTO vault_meta(key, value) VALUES ($key, 'rolled-back');";
            lease.Command.Parameters.AddWithValue("$key", key);
            await lease.Command.ExecuteNonQueryAsync();
        }

        await context.DisposeAsync();

        Assert.Null(await fixture.ReadCommittedValueAsync(key));
    }

    [Fact]
    public async Task ContextRejectsSecondActiveCommand()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var context = fixture.Runtime.CreateOperationContext();
        using var first = context.CreateCommand();

        var exception = Assert.Throws<VaultException>(() => context.CreateCommand());

        Assert.Equal(VaultErrorCode.VaultBusy, exception.Code);
        await context.DisposeAsync();
    }

    [Fact]
    public async Task CommandLeaseUnregistersOnDispose()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var context = fixture.Runtime.CreateOperationContext();
        var lease = context.CreateCommand();
        Assert.True(fixture.Runtime.HasActiveCommandForTest);

        lease.Dispose();

        Assert.False(fixture.Runtime.HasActiveCommandForTest);
        await context.DisposeAsync();
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(
            string root,
            VaultKeyMaterial material,
            VaultDatabaseRuntime runtime)
        {
            Root = root;
            Material = material;
            Runtime = runtime;
        }

        internal string Root { get; }

        internal VaultKeyMaterial Material { get; }

        internal VaultDatabaseRuntime Runtime { get; }

        internal static async Task<RuntimeFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-context-{Guid.NewGuid():N}");
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
                return new RuntimeFixture(root, material, runtime);
            }
            catch
            {
                material.Dispose();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        internal async Task<string?> ReadCommittedValueAsync(string key)
        {
            await using var observer =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    Root,
                    Material,
                    create: false);
            using var command = observer.Connection.CreateCommand();
            command.CommandText = "SELECT value FROM vault_meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? null : Convert.ToString(result);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            Material.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
