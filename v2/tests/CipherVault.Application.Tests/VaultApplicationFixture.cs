using CipherVault.Application.Audit;
using CipherVault.Application.Documents;
using CipherVault.Application.Graph;
using CipherVault.Application.Import;
using CipherVault.Application.Search;
using CipherVault.Application.Vault;

namespace CipherVault.Application.Tests;

internal sealed class VaultApplicationFixture : IAsyncDisposable
{
    private const string Password = "correct horse battery staple";

    private VaultApplicationFixture(
        string parent,
        VaultSessionManager manager,
        TimeProvider timeProvider)
    {
        Parent = parent;
        Manager = manager;
        TimeProvider = timeProvider;
        Documents = new DocumentService(manager, timeProvider);
        Trash = new TrashService(manager, timeProvider);
        Revisions = new DocumentRevisionService(manager, timeProvider);
        Search = new SearchService(manager, timeProvider);
        Graph = new GraphService(manager);
        Import = new ImportService(manager, timeProvider);
        Audit = new AuditQueryService(manager, timeProvider);
    }

    internal string Parent { get; }

    internal string VaultDirectory => Path.Combine(Parent, "vault");

    internal VaultSessionManager Manager { get; }

    internal TimeProvider TimeProvider { get; }

    internal DocumentService Documents { get; }

    internal TrashService Trash { get; }

    internal DocumentRevisionService Revisions { get; }

    internal SearchService Search { get; }

    internal GraphService Graph { get; }

    internal ImportService Import { get; }

    internal AuditQueryService Audit { get; }

    internal static async Task<VaultApplicationFixture> CreateAsync(
        TimeProvider? timeProvider = null)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-phase3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        timeProvider ??= new ManualTimeProvider();
        var manager = await VaultSessionManager.CreateAsync(
            Path.Combine(parent, "vault"),
            VaultSessionOptions.Default,
            timeProvider);
        try
        {
            await manager.InitializeAsync(Password);
            await manager.UnlockAsync(Password);
            return new VaultApplicationFixture(parent, manager, timeProvider);
        }
        catch
        {
            await manager.DisposeAsync();
            await TestDirectoryCleanup.DeleteBestEffortAsync(parent);
            throw;
        }
    }

    internal async Task<T> QueryAsync<T>(
        Func<CipherVault.Core.Database.VaultOperationContext, Task<T>> query)
    {
        return await Manager.ExecuteAsync(
            async (context, _) => await query(context));
    }

    internal async Task RelockAsync()
    {
        _ = await Manager.LockAsync();
        _ = await Manager.UnlockAsync(Password);
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await TestDirectoryCleanup.DeleteBestEffortAsync(Parent);
    }
}
