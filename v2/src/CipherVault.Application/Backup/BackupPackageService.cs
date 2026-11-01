using CipherVault.Core.Backup;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Application.Backup;

internal sealed record BackupPackageBuildResult(
    BackupListItem Item,
    RegularFileIdentity Identity,
    string WholeFileSha256);

internal sealed class BackupPackageService
{
    public async Task<BackupPackageBuildResult> BuildAndVerifyAsync(
        BackupSnapshotCapture snapshot,
        string finalFileName,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
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
        try
        {
            var written = await BackupPackageWriter.WriteAsync(
                tempPath,
                snapshot.VaultId,
                snapshot.CapturedAtUtc,
                snapshot.DatabasePath,
                snapshot.KeyEnvelopePath,
                cancellationToken);
            using var credential = new SnapshotDatabaseKeyCredential(
                snapshot.DatabaseKeyLease.DatabaseKey);
            var verification = await BackupPackageVerifier.VerifyAsync(
                new BackupVerificationRequest(
                    tempPath,
                    verifyDirectory,
                    credential,
                    RequireCurrentSchema: true),
                cancellationToken);
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

            File.Move(tempPath, finalPath, overwrite: false);
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
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Backup package validation failed.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to publish the backup package.",
                exception);
        }
        finally
        {
            DeleteFileBestEffort(tempPath);
            DeleteDirectoryBestEffort(verifyDirectory);
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // A strict .partial file is never listed as a valid backup.
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
            // Verification material remains encrypted and outside final names.
        }
    }
}
