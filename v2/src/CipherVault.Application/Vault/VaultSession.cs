using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Vault;

internal sealed class VaultSession
{
    private readonly object backupActivitySync = new();
    private readonly object writeModeSync = new();
    private int activeBackupOperationCount;
    private TaskCompletionSource? backupOperationsDrained;
    private bool backupCleanupFailed;
    private McpWriteMode mcpWriteMode = McpWriteMode.Draft;
    private DateTimeOffset mcpWriteModeUpdatedAt;

    internal VaultSession(
        Guid sessionId,
        long generation,
        VaultDatabaseRuntime database,
        VaultKeyMaterial keyMaterial,
        DateTimeOffset startedAtUtc,
        VaultActivityState activity)
    {
        SessionId = sessionId;
        Generation = generation;
        Database = database;
        KeyMaterial = keyMaterial;
        StartedAtUtc = startedAtUtc;
        Activity = activity;
        mcpWriteModeUpdatedAt = startedAtUtc;
    }

    internal Guid SessionId { get; }

    internal long Generation { get; }

    internal Guid VaultId => Database.VaultId;

    internal VaultDatabaseRuntime Database { get; }

    internal VaultKeyMaterial KeyMaterial { get; }

    internal DateTimeOffset StartedAtUtc { get; }

    internal VaultActivityState Activity { get; }

    internal McpWriteModeSnapshot GetMcpWriteMode()
    {
        lock (writeModeSync)
        {
            return new McpWriteModeSnapshot(
                mcpWriteMode,
                SessionId,
                Generation,
                mcpWriteModeUpdatedAt,
                Effective: true);
        }
    }

    internal void SetMcpWriteMode(
        McpWriteMode mode,
        DateTimeOffset updatedAt)
    {
        lock (writeModeSync)
        {
            mcpWriteMode = mode;
            mcpWriteModeUpdatedAt = updatedAt.ToUniversalTime();
        }
    }

    internal CancellationTokenSource OperationCancellation { get; } = new();

    internal bool HasActiveBackupOperationsForTest
    {
        get
        {
            lock (backupActivitySync)
            {
                return activeBackupOperationCount > 0;
            }
        }
    }

    internal bool BackupCleanupFailed
    {
        get
        {
            lock (backupActivitySync)
            {
                return backupCleanupFailed;
            }
        }
    }

    internal Task BackupOperationsDrainedTask
    {
        get
        {
            lock (backupActivitySync)
            {
                return activeBackupOperationCount == 0
                    ? Task.CompletedTask
                    : backupOperationsDrained!.Task;
            }
        }
    }

    internal void RegisterBackupOperation()
    {
        lock (backupActivitySync)
        {
            if (activeBackupOperationCount == 0)
            {
                backupOperationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            activeBackupOperationCount++;
        }
    }

    internal void CompleteBackupOperation(bool cleanupSucceeded)
    {
        lock (backupActivitySync)
        {
            if (activeBackupOperationCount <= 0)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "There is no active backup operation to complete.");
            }

            if (!cleanupSucceeded)
            {
                backupCleanupFailed = true;
            }

            activeBackupOperationCount--;
            if (activeBackupOperationCount == 0)
            {
                backupOperationsDrained!.TrySetResult();
            }
        }
    }

    internal async ValueTask ReleaseAsync()
    {
        lock (backupActivitySync)
        {
            if (activeBackupOperationCount != 0)
            {
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "Vault Session cannot be released while backup activity is active.");
            }
        }

        await Database.DisposeAsync();
        KeyMaterial.Dispose();
        OperationCancellation.Dispose();
    }
}
