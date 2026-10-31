using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Database;

internal sealed class VaultCommandLease : IDisposable, IAsyncDisposable
{
    private readonly VaultOperationContext context;
    private int disposed;

    internal VaultCommandLease(
        VaultOperationContext context,
        SqliteCommand command)
    {
        this.context = context;
        Command = command;
    }

    internal SqliteCommand Command { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        context.UnregisterCommand(Command);
        Command.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
