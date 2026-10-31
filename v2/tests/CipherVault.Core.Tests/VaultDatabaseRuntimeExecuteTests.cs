using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultDatabaseRuntimeExecuteTests
{
    [Fact]
    public async Task ExecuteAsyncReturnsMaterializedResultAndRefreshesNoState()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Runtime.ExecuteAsync(
            (context, cancellationToken) =>
            {
                Assert.Equal(fixture.Runtime.VaultId, context.VaultId);
                Assert.False(context.IsDisposed);
                return ValueTask.FromResult(new MaterializedResult(1, "safe"));
            });

        Assert.Equal(new MaterializedResult(1, "safe"), result);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsReentryWithoutWaiting()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<VaultException>(async () =>
            await fixture.Runtime.ExecuteAsync(async (_, _) =>
            {
                await fixture.Runtime.ExecuteAsync(
                    (_, _) => ValueTask.FromResult(1));
                return 1;
            }));

        Assert.Equal(VaultErrorCode.VaultBusy, exception.Code);
    }

    [Fact]
    public async Task ExecuteAsyncRollsBackWhenCallbackThrows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var key = $"tx-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<VaultException>(async () =>
            await fixture.Runtime.ExecuteAsync<int>(async (context, cancellationToken) =>
            {
                await context.BeginTransactionAsync(cancellationToken);
                using var lease = context.CreateCommand();
                lease.Command.CommandText =
                    "INSERT INTO vault_meta(key, value) VALUES ($key, 'rollback');";
                lease.Command.Parameters.AddWithValue("$key", key);
                await lease.Command.ExecuteNonQueryAsync(cancellationToken);
                throw new InvalidOperationException("intentional");
            }));

        Assert.Equal(VaultErrorCode.DatabaseError, exception.Code);
        Assert.Null(await fixture.ReadCommittedValueAsync(key));
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotCommitWhenResultIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var key = $"tx-{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<VaultException>(async () =>
            await fixture.Runtime.ExecuteAsync<object>(async (context, cancellationToken) =>
            {
                await context.BeginTransactionAsync(cancellationToken);
                using (var lease = context.CreateCommand())
                {
                    lease.Command.CommandText =
                        "INSERT INTO vault_meta(key, value) VALUES ($key, 'rejected');";
                    lease.Command.Parameters.AddWithValue("$key", key);
                    await lease.Command.ExecuteNonQueryAsync(cancellationToken);
                }

                await context.CommitTransactionAsync(cancellationToken);
                return context;
            }));

        Assert.Null(await fixture.ReadCommittedValueAsync(key));
    }

    [Fact]
    public async Task ExecuteAsyncRejectsUndisposedCommandAndRollsBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        var key = $"tx-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<VaultException>(async () =>
            await fixture.Runtime.ExecuteAsync(async (context, cancellationToken) =>
            {
                await context.BeginTransactionAsync(cancellationToken);
#pragma warning disable CA2000
                var lease = context.CreateCommand();
#pragma warning restore CA2000
                lease.Command.CommandText =
                    "INSERT INTO vault_meta(key, value) VALUES ($key, 'active');";
                lease.Command.Parameters.AddWithValue("$key", key);
                await lease.Command.ExecuteNonQueryAsync(cancellationToken);
                await context.CommitTransactionAsync(cancellationToken);
                return 1;
            }));

        Assert.Equal(VaultErrorCode.DatabaseError, exception.Code);
        Assert.Null(await fixture.ReadCommittedValueAsync(key));
    }

    private sealed record MaterializedResult(int Number, string Text);

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
                $"cipher-vault-execute-{Guid.NewGuid():N}");
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
