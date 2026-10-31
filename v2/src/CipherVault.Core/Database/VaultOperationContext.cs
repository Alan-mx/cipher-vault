using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Database;

public sealed class VaultOperationContext
{
    private readonly VaultDatabaseRuntime runtime;
    private readonly SemaphoreSlim transactionGate = new(1, 1);
    private SqliteTransaction? transaction;
    private bool commitRequested;
    private int disposed;

    internal VaultOperationContext(
        VaultDatabaseRuntime runtime,
        SqliteConnection connection)
    {
        this.runtime = runtime;
        Connection = connection;
        VaultId = runtime.VaultId;
        SessionId = runtime.SessionId;
        SessionGeneration = runtime.Generation;
    }

    public Guid VaultId { get; }

    public Guid SessionId { get; }

    public long SessionGeneration { get; }

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal SqliteConnection Connection { get; }

    internal SqliteTransaction? Transaction
    {
        get
        {
            lock (transactionGate)
            {
                return transaction;
            }
        }
    }

    public async ValueTask BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await transactionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (transaction is not null || commitRequested)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "A transaction is already active or pending commit.");
            }

            var started = await Connection.BeginTransactionAsync(cancellationToken);
            transaction = (SqliteTransaction)started;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VaultException)
        {
            throw;
        }
        catch (SqliteException)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Unable to begin the Vault transaction.");
        }
        finally
        {
            transactionGate.Release();
        }
    }

    public async ValueTask CommitTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await transactionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (transaction is null || commitRequested)
            {
                throw new VaultException(
                    VaultErrorCode.DatabaseError,
                    "There is no transaction available to commit.");
            }

            commitRequested = true;
        }
        finally
        {
            transactionGate.Release();
        }
    }

    public async ValueTask RollbackTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await transactionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (transaction is null)
            {
                return;
            }

            await RollbackAndClearAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Unable to roll back the Vault transaction.");
        }
        finally
        {
            transactionGate.Release();
        }
    }

    internal VaultCommandLease CreateCommand()
    {
        ThrowIfDisposed();
        if (commitRequested)
        {
            throw new VaultException(
                VaultErrorCode.VaultBusy,
                "The operation has already requested a transaction commit.");
        }

        var command = Connection.CreateCommand();
        command.Transaction = transaction;
        try
        {
            runtime.RegisterExecutingCommand(command);
            return new VaultCommandLease(this, command);
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    internal void UnregisterCommand(SqliteCommand command) =>
        runtime.UnregisterExecutingCommand(command);

    internal async ValueTask FinalizeTransactionAsync(
        bool operationSucceeded,
        CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return;
        }

        await transactionGate.WaitAsync(cancellationToken);
        try
        {
            if (transaction is null)
            {
                return;
            }

            try
            {
                if (operationSucceeded && commitRequested)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SqliteException)
            {
                throw new VaultException(
                    VaultErrorCode.DatabaseError,
                    "Unable to finalize the Vault transaction.");
            }
            finally
            {
                await transaction.DisposeAsync();
                transaction = null;
                commitRequested = false;
            }
        }
        finally
        {
            transactionGate.Release();
        }
    }

    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await transactionGate.WaitAsync();
        try
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                catch (SqliteException)
                {
                    // The runtime still releases the connection after cleanup.
                }
                finally
                {
                    await transaction.DisposeAsync();
                    transaction = null;
                }
            }
        }
        finally
        {
            transactionGate.Release();
            transactionGate.Dispose();
        }
    }

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }

    private async ValueTask RollbackAndClearAsync(CancellationToken cancellationToken)
    {
        await transaction!.RollbackAsync(cancellationToken);
        await transaction.DisposeAsync();
        transaction = null;
        commitRequested = false;
    }
}
