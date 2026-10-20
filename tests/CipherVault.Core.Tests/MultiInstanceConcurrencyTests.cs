using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;

namespace CipherVault.Core.Tests;

public sealed class MultiInstanceConcurrencyTests
{
    private const string Password = "multi-instance-password";

    [Fact]
    public async Task LockingOneInstanceDoesNotLockAnother()
    {
        await using var fixture = await TwoInstanceFixture.CreateAsync();

        await fixture.First.LockAsync();

        Assert.Equal(
            VaultSessionState.Locked,
            (await fixture.First.GetStatusAsync()).State);
        Assert.Equal(
            VaultSessionState.Unlocked,
            (await fixture.Second.GetStatusAsync()).State);
        var catalog = await fixture.Second.ListCatalogAsync(null, null, 10);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task ConcurrentCreateOfSamePathAllowsOnlyOneWriter()
    {
        await using var fixture = await TwoInstanceFixture.CreateAsync();

        var results = await RunConcurrentlyAsync(
            () => fixture.First.CreateNoteAsync(
                "same path",
                null,
                "md",
                "first",
                null,
                VaultActorKind.UserUi),
            () => fixture.Second.CreateNoteAsync(
                "same path",
                null,
                "md",
                "second",
                null,
                VaultActorKind.UserUi));

        Assert.Equal(1, results.Count(result => result.Success));
        Assert.Equal(
            1,
            results.Count(result => result.ErrorCode == VaultErrorCode.Conflict));
        Assert.True((await fixture.First.VerifyVaultAsync()).Valid);
        var catalog = await fixture.First.ListCatalogAsync(null, null, 10);
        Assert.Single(catalog.Items, item => item.Title == "same path");
    }

    [Fact]
    public async Task ConcurrentAppendWithSameRevisionAllowsOnlyOneWriter()
    {
        await using var fixture = await TwoInstanceFixture.CreateAsync();
        var created = await fixture.First.CreateNoteAsync(
            "revision race",
            null,
            "md",
            "initial",
            null,
            VaultActorKind.UserUi);

        var results = await RunConcurrentlyAsync(
            () => fixture.First.AppendNoteAsync(
                created.DocumentId,
                created.Revision,
                " first",
                VaultActorKind.UserUi),
            () => fixture.Second.AppendNoteAsync(
                created.DocumentId,
                created.Revision,
                " second",
                VaultActorKind.UserUi));

        Assert.Equal(1, results.Count(result => result.Success));
        Assert.Equal(
            1,
            results.Count(result => result.ErrorCode == VaultErrorCode.Conflict));
        Assert.True((await fixture.Second.VerifyVaultAsync()).Valid);
    }

    [Fact]
    public async Task ConcurrentCreateOfDifferentPathsPersistsBothDocuments()
    {
        await using var fixture = await TwoInstanceFixture.CreateAsync();

        var results = await RunConcurrentlyAsync(
            () => fixture.First.CreateNoteAsync(
                "first instance",
                "instances",
                "md",
                "first",
                null,
                VaultActorKind.UserUi),
            () => fixture.Second.CreateNoteAsync(
                "second instance",
                "instances",
                "md",
                "second",
                null,
                VaultActorKind.UserUi));

        Assert.All(results, result => Assert.True(result.Success));
        var verification = await fixture.First.VerifyVaultAsync();
        Assert.True(verification.Valid);
        var catalog = await fixture.First.ListCatalogAsync("instances", null, 10);
        Assert.Contains(catalog.Items, item => item.Title == "first instance");
        Assert.Contains(catalog.Items, item => item.Title == "second instance");
        var audit = await fixture.First.GetAuditPageAsync(null, 20);
        Assert.Equal(2, audit.Items.Count(item => item.Action == "create"));
    }

    private static async Task<IReadOnlyList<OperationResult>> RunConcurrentlyAsync(
        params Func<Task>[] operations)
    {
        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(async operation =>
        {
            await start.Task;
            try
            {
                await operation();
                return new OperationResult(true, null);
            }
            catch (VaultException exception)
            {
                return new OperationResult(false, exception.Code);
            }
        }).ToArray();

        start.SetResult(true);
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed record OperationResult(bool Success, VaultErrorCode? ErrorCode);

    private sealed class TwoInstanceFixture : IAsyncDisposable
    {
        private TwoInstanceFixture(
            string rootPath,
            string vaultPath,
            VaultService first,
            VaultService second)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            First = first;
            Second = second;
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public VaultService First { get; }
        public VaultService Second { get; }

        public static async Task<TwoInstanceFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-multi-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var first = new VaultService(new AlwaysActiveActivityProvider());
            var second = new VaultService(new AlwaysActiveActivityProvider());
            try
            {
                var initialized = await first.InitializeAsync(vaultPath, Password);
                await first.ConfirmRecoveryKeyAsync(
                    vaultPath,
                    initialized.RecoveryKey,
                    VaultActorKind.UserUi);
                await first.UnlockAsync(
                    vaultPath,
                    Password,
                    VaultCredentialType.Password);
                await second.UnlockAsync(
                    vaultPath,
                    Password,
                    VaultCredentialType.Password);
                return new TwoInstanceFixture(root, vaultPath, first, second);
            }
            catch
            {
                await second.DisposeAsync();
                await first.DisposeAsync();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Second.DisposeAsync();
            await First.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
