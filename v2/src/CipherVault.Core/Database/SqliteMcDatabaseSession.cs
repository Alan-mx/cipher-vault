using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Database;

public sealed class SqliteMcDatabaseSession : IAsyncDisposable
{
    private static int activeSessionCount;
    private int disposed;

    internal SqliteMcDatabaseSession(Guid vaultId, SqliteConnection connection)
    {
        VaultId = vaultId;
        Connection = connection;
        Interlocked.Increment(ref activeSessionCount);
    }

    public Guid VaultId { get; }

    public VaultDatabaseInfo DatabaseInfo { get; internal set; } = null!;

    internal SqliteConnection Connection { get; }

    internal static int ActiveSessionCountForTest => Volatile.Read(ref activeSessionCount);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Connection.Close();
        }
        finally
        {
            try
            {
                await Connection.DisposeAsync();
            }
            finally
            {
                Interlocked.Decrement(ref activeSessionCount);
            }
        }
    }
}
