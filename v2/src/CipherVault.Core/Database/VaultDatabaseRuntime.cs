using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CipherVault.Core.Database;

public sealed class VaultDatabaseRuntime : IAsyncDisposable
{
    private readonly SqliteMcDatabaseSession session;
    private readonly object lifecycleSync = new();
    private readonly object activeCommandSync = new();
    private SqliteCommand? activeCommand;
    private int executing;
    private Task? disposeTask;

    internal VaultDatabaseRuntime(
        Guid sessionId,
        long generation,
        SqliteMcDatabaseSession session)
    {
        SessionId = sessionId;
        Generation = generation;
        this.session = session;
        DatabaseInfo = session.DatabaseInfo;
    }

    public Guid VaultId => DatabaseInfo.VaultId;

    public Guid SessionId { get; }

    public long Generation { get; }

    public VaultDatabaseInfo DatabaseInfo { get; }

    internal bool IsDisposedForTest
    {
        get
        {
            lock (lifecycleSync)
            {
                return disposeTask is not null;
            }
        }
    }

    internal System.Data.ConnectionState ConnectionStateForTest =>
        session.Connection.State;

    public async ValueTask<T> ExecuteAsync<T>(
        Func<VaultOperationContext, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (lifecycleSync)
        {
            if (disposeTask is not null)
            {
                throw new ObjectDisposedException(nameof(VaultDatabaseRuntime));
            }

            if (executing != 0)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "The Vault database runtime is already executing an operation.");
            }

            executing = 1;
        }

        VaultOperationContext? context = null;
        var operationSucceeded = false;
        try
        {
            context = CreateOperationContext();
            var result = await operation(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (CleanupActiveCommand())
            {
                throw new VaultException(
                    VaultErrorCode.DatabaseError,
                    "The database operation left an active command inside the operation boundary.");
            }

            VaultOperationResultGuard.ValidateResult(result);
            cancellationToken.ThrowIfCancellationRequested();
            await context.FinalizeTransactionAsync(
                operationSucceeded: true,
                CancellationToken.None);
            operationSucceeded = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            if (context is not null && !operationSucceeded)
            {
                try
                {
                    await context.FinalizeTransactionAsync(
                        operationSucceeded: false,
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original operation or guard failure.
                }
            }

            throw;
        }
        catch (VaultException)
        {
            if (context is not null && !operationSucceeded)
            {
                try
                {
                    await context.FinalizeTransactionAsync(
                        operationSucceeded: false,
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original operation failure.
                }
            }

            throw;
        }
        catch
        {
            if (context is not null && !operationSucceeded)
            {
                try
                {
                    await context.FinalizeTransactionAsync(
                        operationSucceeded: false,
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the stable mapped operation failure.
                }
            }

            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Vault database operation failed.");
        }
        finally
        {
            try
            {
                CleanupActiveCommand();
                if (context is not null)
                {
                    await context.DisposeAsync();
                }
            }
            finally
            {
                lock (lifecycleSync)
                {
                    executing = 0;
                }
            }
        }
    }

    internal VaultOperationContext CreateOperationContext()
    {
        EnsureNotDisposed();
        return new VaultOperationContext(this, session.Connection);
    }

    public static async ValueTask<VaultDatabaseRuntime> OpenAsync(
        string vaultDirectory,
        VaultKeyMaterial keyMaterial,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        if (sessionId == Guid.Empty)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Session ID must not be empty.");
        }

        if (generation <= 0)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Session generation must be positive.");
        }

        var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            vaultDirectory,
            keyMaterial,
            create: false,
            cancellationToken);
        if (session.VaultId != keyMaterial.VaultId)
        {
            await session.DisposeAsync();
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault key does not belong to this database.");
        }

        return new VaultDatabaseRuntime(sessionId, generation, session);
    }

    internal void RegisterExecutingCommand(SqliteCommand command)
    {
        lock (activeCommandSync)
        {
            if (activeCommand is not null)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "Another database command is already active.");
            }

            activeCommand = command;
        }
    }

    internal void UnregisterExecutingCommand(SqliteCommand command)
    {
        lock (activeCommandSync)
        {
            if (ReferenceEquals(activeCommand, command))
            {
                activeCommand = null;
            }
        }
    }

    internal bool HasActiveCommandForTest
    {
        get
        {
            lock (activeCommandSync)
            {
                return activeCommand is not null;
            }
        }
    }

    public bool TryInterruptActiveCommand()
    {
        lock (activeCommandSync)
        {
            if (activeCommand is null)
            {
                return false;
            }
        }

        EnsureNotDisposed();
        try
        {
            raw.sqlite3_interrupt(session.Connection.Handle);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool CleanupActiveCommand()
    {
        SqliteCommand? command;
        lock (activeCommandSync)
        {
            command = activeCommand;
            activeCommand = null;
        }

        if (command is null)
        {
            return false;
        }

        try
        {
            raw.sqlite3_interrupt(session.Connection.Handle);
        }
        catch
        {
            // Command disposal below is the authoritative cleanup.
        }

        command.Dispose();
        return true;
    }

    private void EnsureNotDisposed()
    {
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null, this);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (lifecycleSync)
        {
            if (disposeTask is null)
            {
                if (executing != 0)
                {
                    throw new VaultException(
                        VaultErrorCode.VaultBusy,
                        "Unable to dispose the Vault database runtime while an operation is active.");
                }

                disposeTask = DisposeCoreAsync();
            }

            task = disposeTask;
        }

        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync()
    {
        CleanupActiveCommand();
        await session.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
