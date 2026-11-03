using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Application.Backup;

internal enum BackupPackageStage
{
    ZipStarted,
    ZipCompleted,
    VerificationStarted,
    VerificationCompleted,
    PublishCommitRequested,
    PublishCommitted,
    CleanupCompleted
}

internal delegate ValueTask BackupPackageStageObserver(
    BackupPackageStage stage);

internal sealed record BackupPackageBuildResult(
    BackupListItem Item,
    RegularFileIdentity Identity,
    string WholeFileSha256);

internal sealed class BackupPackageService
{
    private readonly BackupPackageStageObserver? observer;

    public BackupPackageService()
    {
    }

    internal BackupPackageService(BackupPackageStageObserver? observer)
    {
        this.observer = observer;
    }

    public async Task<BackupPackageBuildResult> BuildAndVerifyAsync(
        BackupSnapshotCapture snapshot,
        VaultBackupSessionLease backupLease,
        string finalFileName,
        string backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(backupLease);
        _ = BackupFileName.Parse(finalFileName);
        var directory = PathSafety.Canonicalize(
            backupDirectory,
            "Backup directory");
        Directory.CreateDirectory(directory);
        VaultFilePermissions.ProtectDirectory(directory);
        PathSafety.EnsureNoLinkComponents(directory);
        var finalPath = Path.Combine(directory, finalFileName);
        var tempPath = Path.Combine(
            directory,
            $"{BackupConstants.BackupTempPrefix}{Guid.NewGuid():N}{BackupConstants.PartialSuffix}");
        var verifyDirectory = Path.Combine(
            snapshot.WorkingDirectory,
            $"verify-{Guid.NewGuid():N}");
        Exception? operationException = null;
        try
        {
            backupLease.ThrowIfCancellationRequested();
            await NotifyObserverAsync(BackupPackageStage.ZipStarted);
            var written = await BackupPackageWriter.WriteAsync(
                tempPath,
                snapshot.VaultId,
                snapshot.CapturedAtUtc,
                snapshot.DatabasePath,
                snapshot.KeyEnvelopePath,
                backupLease.CancellationToken);
            await NotifyObserverAsync(BackupPackageStage.ZipCompleted);
            backupLease.ThrowIfCancellationRequested();

            await NotifyObserverAsync(BackupPackageStage.VerificationStarted);
            BackupVerificationResult verification;
            using (var credential =
                   backupLease.CreateDatabaseKeyCredential())
            {
                verification = await BackupPackageVerifier.VerifyAsync(
                    new BackupVerificationRequest(
                        tempPath,
                        verifyDirectory,
                        credential,
                        SchemaPolicy: BackupSchemaPolicy.CurrentOnly),
                    backupLease.CancellationToken);
            }

            backupLease.SealDatabaseKeyAccess();
            await NotifyObserverAsync(BackupPackageStage.VerificationCompleted);
            if (verification.Manifest.VaultId != snapshot.VaultId
                || !string.Equals(
                    verification.DatabaseSha256,
                    written.Hashes.DatabaseSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    verification.KeyEnvelopeSha256,
                    written.Hashes.KeyEnvelopeSha256,
                    StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.DatabaseError,
                    "Backup package verification result is inconsistent.");
            }

            await NotifyObserverAsync(
                BackupPackageStage.PublishCommitRequested);
            if (!backupLease.TryBeginPublishCommit())
            {
                backupLease.ThrowIfCancellationRequested();
                throw new VaultException(
                    VaultErrorCode.VaultBusy,
                    "Backup publish commit was not granted.");
            }

            File.Move(tempPath, finalPath, overwrite: false);
            await NotifyObserverAsync(BackupPackageStage.PublishCommitted);
            var identity = RegularFileIdentity.Read(finalPath);
            return new BackupPackageBuildResult(
                new BackupListItem(
                    finalFileName,
                    identity.Bytes,
                    identity.ModifiedAtUtc,
                    snapshot.CapturedAtUtc,
                    BackupConstants.FormatVersion,
                    BackupFileStatus.Verified,
                    null),
                identity,
                verification.WholeFileSha256);
        }
        catch (BackupPackageValidationException exception)
        {
            operationException = new VaultException(
                VaultErrorCode.DatabaseError,
                "Backup package validation failed.",
                exception);
            throw operationException;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            operationException = new VaultException(
                VaultErrorCode.StorageError,
                "Unable to publish the backup package.",
                exception);
            throw operationException;
        }
        catch (Exception exception)
        {
            operationException = exception;
            throw;
        }
        finally
        {
            var lifecycleFailures = new List<Exception>();
            try
            {
                backupLease.SealDatabaseKeyAccess();
            }
            catch (Exception exception)
            {
                lifecycleFailures.Add(exception);
            }

            var cleanupFailures = new List<Exception>();
            await DeleteFileStrictAsync(tempPath, cleanupFailures);
            await DeleteDirectoryStrictAsync(
                verifyDirectory,
                cleanupFailures);
            lifecycleFailures.AddRange(cleanupFailures);
            if (lifecycleFailures.Count > 0)
            {
                var aggregate = new AggregateException(
                    lifecycleFailures);
                backupLease.MarkCleanupFailed(aggregate);
                if (operationException is null)
                {
                    throw new VaultException(
                        VaultErrorCode.StorageError,
                        "Unable to clean backup package resources.",
                        aggregate);
                }

                operationException.Data["CleanupFailures"] = aggregate;
            }

            await NotifyObserverAsync(BackupPackageStage.CleanupCompleted);
        }
    }

    private ValueTask NotifyObserverAsync(BackupPackageStage stage) =>
        observer is null
            ? ValueTask.CompletedTask
            : observer(stage);

    private static async Task DeleteFileStrictAsync(
        string path,
        ICollection<Exception> failures)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                if (attempt == 2)
                {
                    failures.Add(exception);
                    return;
                }

                await Task.Delay(50);
            }
        }
    }

    private static async Task DeleteDirectoryStrictAsync(
        string path,
        ICollection<Exception> failures)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                if (attempt == 2)
                {
                    failures.Add(exception);
                    return;
                }

                await Task.Delay(50);
            }
        }
    }
}
