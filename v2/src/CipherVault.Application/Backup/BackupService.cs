using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Backup;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Application.Backup;

public sealed class BackupService
{
    private readonly BackupStorageOptions options;
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly VerifiedBackupCache verifiedCache = new();
    private readonly BackupTaskCoordinator taskCoordinator = new();
    private readonly BackupPackageService packageService;

    public BackupService(
        BackupStorageOptions options,
        VaultSessionManager sessionManager,
        TimeProvider timeProvider)
        : this(options, sessionManager, timeProvider, observer: null)
    {
    }

    internal BackupService(
        BackupStorageOptions options,
        VaultSessionManager sessionManager,
        TimeProvider timeProvider,
        BackupPackageStageObserver? observer)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.sessionManager = sessionManager
            ?? throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        packageService = new BackupPackageService(observer);
    }

    public async Task<BackupPage> ListAsync(
        BackupQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = await sessionManager.EnsureUnlockedWithoutActivityAsync(
            cancellationToken);
        return await BackupFileStore.ListAsync(
            options.BackupDirectory,
            query,
            verifiedCache,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<BackupCreateResult> CreateAsync(
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!taskCoordinator.TryEnter())
        {
            throw new VaultException(
                VaultErrorCode.BackupInProgress,
                "A backup operation is already in progress.");
        }

        VaultBackupSessionLease? backupLease = null;
        string? stagingDirectory = null;
        var stagingCleaned = false;
        Exception? operationException = null;
        try
        {
            await using var directoryLock = BackupDirectoryLock.Acquire(
                options.BackupLockPath);
            var capturedAtUtc = timeProvider.GetUtcNow();
            var fileName = CreateAvailableFileName(
                capturedAtUtc,
                options.BackupDirectory);

            backupLease = await sessionManager.BeginBackupSessionAsync(
                cancellationToken);
            stagingDirectory = Path.Combine(
                options.BackupDirectory,
                $"{BackupConstants.BackupTempPrefix}{Guid.NewGuid():N}{BackupConstants.PartialSuffix}");
            Directory.CreateDirectory(stagingDirectory);
            VaultFilePermissions.ProtectDirectory(stagingDirectory);

            var snapshot = await sessionManager.CaptureBackupSnapshotAsync(
                backupLease,
                stagingDirectory,
                capturedAtUtc);
            var built = await packageService.BuildAndVerifyAsync(
                snapshot,
                backupLease,
                fileName.Value,
                options.BackupDirectory);

            var cleanupFailures = await DeleteDirectoryStrictAsync(
                stagingDirectory);
            stagingCleaned = cleanupFailures.Count == 0;
            if (!stagingCleaned)
            {
                var cleanupException = new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to clean backup staging resources.",
                    new AggregateException(cleanupFailures));
                backupLease.MarkCleanupFailed(cleanupException);
                throw cleanupException;
            }

            verifiedCache.Set(
                built.Identity.CanonicalPath,
                new VerifiedBackupCacheEntry(
                    built.Identity,
                    snapshot.VaultId,
                    built.WholeFileSha256,
                    timeProvider.GetUtcNow()));
            var auditRecorded = await TryAuditCreateAsync(
                actor,
                built.Item,
                backupLease);
            return new BackupCreateResult(
                built.Item,
                auditRecorded);
        }
        catch (OperationCanceledException exception)
        {
            if (backupLease is not null)
            {
                try
                {
                    backupLease.ThrowIfCancellationRequested();
                }
                catch (VaultException mapped)
                {
                    operationException = mapped;
                    ExceptionDispatchInfo.Capture(mapped).Throw();
                    throw;
                }
                catch (OperationCanceledException mapped)
                {
                    operationException = mapped;
                    ExceptionDispatchInfo.Capture(mapped).Throw();
                    throw;
                }
            }

            operationException = exception;
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            var mapped = new VaultException(
                VaultErrorCode.StorageError,
                "Unable to access backup storage resources.",
                exception);
            operationException = mapped;
            throw mapped;
        }
        catch (Exception exception)
        {
            operationException = exception;
            throw;
        }
        finally
        {
            try
            {
                var cleanupFailures = new List<Exception>();
                if (!stagingCleaned
                    && !string.IsNullOrEmpty(stagingDirectory))
                {
                    cleanupFailures.AddRange(
                        await DeleteDirectoryStrictAsync(stagingDirectory));
                }

                var lifecycleFailures = new List<Exception>();
                if (cleanupFailures.Count > 0 && backupLease is not null)
                {
                    var cleanupException = new AggregateException(
                        cleanupFailures);
                    backupLease.MarkCleanupFailed(cleanupException);
                    lifecycleFailures.Add(cleanupException);
                }

                if (backupLease is not null)
                {
                    try
                    {
                        backupLease.SealDatabaseKeyAccess();
                    }
                    catch (Exception exception)
                    {
                        lifecycleFailures.Add(exception);
                    }

                    try
                    {
                        await backupLease.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        lifecycleFailures.Add(exception);
                    }
                }

                if (lifecycleFailures.Count > 0)
                {
                    var aggregate = new AggregateException(
                        lifecycleFailures);
                    if (operationException is null)
                    {
                        throw new VaultException(
                            VaultErrorCode.StorageError,
                            "Unable to finalize backup lifecycle resources.",
                            aggregate);
                    }

                    operationException.Data["LifecycleFailures"] = aggregate;
                }
            }
            finally
            {
                taskCoordinator.Exit();
            }
        }
    }

    internal async Task<BackupDownloadLease> BeginDownloadAsync(
        string fileName,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return await sessionManager.ExecuteDownloadAdmissionAsync(
            async (context, token) =>
            {
                var lease = await BackupFileStore.OpenDownloadAsync(
                    options.BackupDirectory,
                    fileName,
                    token);
                try
                {
                    await context.BeginTransactionAsync(token);
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "backup.download",
                        targetObjectType: "backup",
                        targetId: null,
                        revision: null,
                        result: "started",
                        details: new
                        {
                            downloadId = lease.DownloadId.ToString("D"),
                            expectedBytes = lease.Bytes
                        },
                        occurredAt: timeProvider.GetUtcNow(),
                        token);
                    await context.CommitTransactionNowAsync(token);
                    return lease;
                }
                catch
                {
                    await TryRollback(context);
                    await lease.DisposeAsync();
                    throw;
                }
            },
            cancellationToken);
    }

    internal async Task<bool> RecordDownloadTerminalAsync(
        BackupDownloadLease lease,
        VaultActorContext actor,
        string outcome,
        long bytesWritten,
        string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(actor);
        if (!lease.TryBeginTerminalAttempt())
        {
            return false;
        }

        try
        {
            return await sessionManager.TryExecuteAuditNoActivityAsync(
                async (context, token) =>
                {
                    await context.BeginTransactionAsync(token);
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "backup.download",
                        targetObjectType: "backup",
                        targetId: null,
                        revision: null,
                        result: outcome,
                        details: new
                        {
                            downloadId = lease.DownloadId.ToString("D"),
                            bytesWritten,
                            errorCode
                        },
                        occurredAt: timeProvider.GetUtcNow(),
                        token);
                    await context.CommitTransactionNowAsync(token);
                },
                TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "Backup download terminal audit failed: {0}",
                exception);
            return false;
        }
    }

    private BackupFileName CreateAvailableFileName(
        DateTimeOffset nowUtc,
        string backupDirectory)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = BackupFileName.Create(nowUtc);
            if (!File.Exists(Path.Combine(
                    backupDirectory,
                    candidate.Value)))
            {
                return candidate;
            }
        }

        throw new VaultException(
            VaultErrorCode.StorageError,
            "Unable to allocate a unique backup filename.");
    }

    private async Task<bool> TryAuditCreateAsync(
        VaultActorContext actor,
        BackupListItem item,
        VaultBackupSessionLease backupLease)
    {
        if (backupLease.CancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning(
                "Backup create audit skipped because the backup was cancelled.");
            return false;
        }

        try
        {
            await sessionManager.ExecuteAsync(
                async (context, token) =>
                {
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "backup.create",
                        targetObjectType: "backup",
                        targetId: null,
                        revision: null,
                        result: "success",
                        details: new
                        {
                            bytes = item.Bytes,
                            formatVersion = item.FormatVersion,
                            status = "verified"
                        },
                        occurredAt: timeProvider.GetUtcNow(),
                        token);
                    return true;
                },
                backupLease.CancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is VaultException or OperationCanceledException)
        {
            Trace.TraceError(
                "Backup create audit failed: {0}",
                exception);
            return false;
        }
    }

    private static async Task<List<Exception>> DeleteDirectoryStrictAsync(
        string path)
    {
        var failures = new List<Exception>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return failures;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                if (attempt == 2)
                {
                    failures.Add(exception);
                    return failures;
                }

                await Task.Delay(50);
            }
        }

        return failures;
    }

    private static async Task TryRollback(VaultOperationContext context)
    {
        try
        {
            await context.RollbackTransactionAsync(CancellationToken.None);
        }
        catch
        {
            // The operation boundary still owns final transaction cleanup.
        }
    }
}
