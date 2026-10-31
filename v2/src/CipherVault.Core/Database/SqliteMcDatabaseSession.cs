using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Database;

public sealed class SqliteMcDatabaseSession : IAsyncDisposable
{
    internal SqliteMcDatabaseSession(Guid vaultId, SqliteConnection connection)
    {
        VaultId = vaultId;
        Connection = connection;
    }

    public Guid VaultId { get; }

    public VaultDatabaseInfo DatabaseInfo { get; internal set; } = null!;

    internal SqliteConnection Connection { get; }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
    }
}
