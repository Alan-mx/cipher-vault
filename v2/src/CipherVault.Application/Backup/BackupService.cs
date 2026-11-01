using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Core.Audit;
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
    private readonly BackupPackageService packageService = new();

    public BackupService(
        BackupStorageOptions options,
        VaultSessionManager sessionManager,
        TimeProvider timeProvider)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.sessionManager = sessionManager
            ?? throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
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

        try
        {
            await using var directoryLock = BackupDirectoryLock.Acquire(
                options.BackupLockPath);
            var capturedAtUtc = timeProvider.GetUtcNow();
            var fileName = CreateAvailableFileName(
                capturedAtUtc,
                options.BackupDirectory);
            var stagingDirectory = Path.Combine(
                options.BackupDirectory,
                $"{BackupConstants.BackupTempPrefix}{Guid.NewGuid():N}{BackupConstants.PartialSuffix}");
            Directory.CreateDirectory(options.BackupDirectory);
            VaultFilePermissions.ProtectDirectory(
                options.BackupDirectory);
            Directory.CreateDirectory(stagingDirectory);
            VaultFilePermissions.ProtectDirectory(stagingDirectory);
            BackupSnapshotCapture? snapshot = null;
            try
            {
                snapshot = await sessionManager.CaptureBackupSnapshotAsync(
                    stagingDirectory,
                    capturedAtUtc,
                    cancellationToken);
                try
                {
                    var built = await packageService.BuildAndVerifyAsync(
                        snapshot,
                        fileName.Value,
                        options.BackupDirectory,
                        cancellationToken);
                    verifiedCache.Set(
                        built.Identity.CanonicalPath,
                        new VerifiedBackupCacheEntry(
                            built.Identity,
                            snapshot.VaultId,
                            built.WholeFileSha256,
                            timeProvider.GetUtcNow()));
                    var auditRecorded = await TryAuditCreateAsync(
                        actor,
                        built.Item);
                    return new BackupCreateResult(
                        built.Item,
                        auditRecorded);
                }
                finally
                {
                    snapshot!.DatabaseKeyLease.Dispose();
                }
            }
            finally
            {
                DeleteDirectoryBestEffort(stagingDirectory);
            }
        }
        finally
        {
            taskCoordinator.Exit();
        }
    }

    public async Task<BackupDownloadLease> OpenDownloadAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        _ = await sessionManager.EnsureUnlockedWithoutActivityAsync(
            cancellationToken);
        return await BackupFileStore.OpenDownloadAsync(
            options.BackupDirectory,
            fileName,
            cancellationToken);
    }

    public async Task<bool> RecordDownloadAsync(
        BackupDownloadLease lease,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await sessionManager.ExecuteAsync(
                async (context, token) =>
                {
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "backup.download",
                        documentId: null,
                        revision: null,
                        result: "success",
                        details: new
                        {
                            bytes = lease.Bytes
                        },
                        occurredAt: timeProvider.GetUtcNow(),
                        token);
                    return true;
                },
                CancellationToken.None);
            return true;
        }
        catch (VaultException)
        {
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
        BackupListItem item)
    {
        try
        {
            await sessionManager.ExecuteAsync(
                async (context, token) =>
                {
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "backup.create",
                        documentId: null,
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
                CancellationToken.None);
            return true;
        }
        catch (VaultException)
        {
            return false;
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // Strict .partial staging is never exposed as a valid backup.
        }
    }
}
