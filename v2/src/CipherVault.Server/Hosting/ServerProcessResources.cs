using CipherVault.Application.Vault;
using CipherVault.Core.Database;

namespace CipherVault.Server.Hosting;

internal sealed class ServerProcessResources : IAsyncDisposable
{
    private readonly Func<ValueTask> shutdownSqlite;
    private VaultSessionManager? manager;
    private bool sqliteTouched;
    private int disposed;

    public ServerProcessResources(
        Func<ValueTask>? shutdownSqlite = null)
    {
        this.shutdownSqlite = shutdownSqlite
            ?? (() =>
            {
                _ = SqliteMcDatabaseFactory.ShutdownProcess();
                return ValueTask.CompletedTask;
            });
    }

    public void AttachManager(VaultSessionManager sessionManager)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (manager is not null)
        {
            throw new InvalidOperationException(
                "A VaultSessionManager is already attached.");
        }

        manager = sessionManager;
    }

    public void MarkSqliteTouched()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        sqliteTouched = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var current = Interlocked.Exchange(ref manager, null);
        try
        {
            if (current is not null)
            {
                await current.DisposeAsync();
            }
        }
        finally
        {
            if (sqliteTouched)
            {
                await shutdownSqlite();
            }
        }
    }
}
