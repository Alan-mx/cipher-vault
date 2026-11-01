using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Backup;

public static class BackupFileStore
{
    public static async Task<BackupPage> ListAsync(
        string backupDirectory,
        BackupQuery query,
        VerifiedBackupCache cache,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(cache);
        var directory = PathSafety.Canonicalize(
            backupDirectory,
            "Backup directory");
        PathSafety.EnsureNoLinkComponents(directory);
        if (!Directory.Exists(directory))
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Backup directory is unavailable.");
        }

        var pageSize = NormalizePageSize(query.Page?.PageSize);
        var fingerprint = CursorCodec.Fingerprint(
            "backup-list-v1",
            directory);
        var hasCursor = CursorCodec.TryDecode(
            query.Page?.Cursor,
            "backup-list",
            fingerprint,
            out BackupCursorPayload? cursor);
        if (query.Page?.Cursor is not null && !hasCursor)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Backup cursor is invalid.");
        }

        var snapshotAt = cursor?.SnapshotAtUtc ?? nowUtc.ToUniversalTime();
        var entries = new List<BackupListEntry>();
        string[] files;
        try
        {
            files = Directory.GetFiles(
                directory,
                $"{BackupConstants.BackupFilePrefix}*.cvbackup",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to enumerate backups.",
                exception);
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!BackupFileName.TryParse(fileName, out _)
                || !PathSafety.IsRegularFile(path))
            {
                continue;
            }

            var entry = await InspectFileAsync(
                path,
                fileName,
                cache,
                cancellationToken);
            if (entry is not null
                && entry.EffectiveCreatedAtUtc <= snapshotAt)
            {
                entries.Add(entry);
            }
        }

        var ordered = entries
            .Where(entry => cursor is null
                || entry.EffectiveCreatedAtUtc < cursor.EffectiveCreatedAtUtc
                || entry.EffectiveCreatedAtUtc == cursor.EffectiveCreatedAtUtc
                    && string.CompareOrdinal(
                        entry.Item.FileName,
                        cursor.FileName) < 0)
            .OrderByDescending(entry => entry.EffectiveCreatedAtUtc)
            .ThenByDescending(
                entry => entry.Item.FileName,
                StringComparer.Ordinal)
            .Take(pageSize + 1)
            .ToArray();
        var hasMore = ordered.Length > pageSize;
        if (hasMore)
        {
            ordered = ordered[..pageSize];
        }

        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "backup-list",
                fingerprint,
                new BackupCursorPayload(
                    snapshotAt,
                    ordered[^1].EffectiveCreatedAtUtc,
                    ordered[^1].Item.FileName))
            : null;
        return new BackupPage(
            ordered.Select(entry => entry.Item).ToArray(),
            nextCursor);
    }

    public static Task<BackupDownloadLease> OpenDownloadAsync(
        string backupDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        _ = BackupFileName.Parse(fileName);
        var directory = PathSafety.Canonicalize(
            backupDirectory,
            "Backup directory");
        var path = PathSafety.Canonicalize(
            Path.Combine(directory, fileName),
            "Backup file");
        if (!PathSafety.GetRelation(path, directory).IsChildOf)
        {
            throw new VaultException(
                VaultErrorCode.BackupNotFound,
                "Backup file was not found.");
        }

        try
        {
            PathSafety.EnsureNoLinkComponents(path);
            if (!PathSafety.IsRegularFile(path))
            {
                throw new VaultException(
                    VaultErrorCode.BackupNotFound,
                    "Backup file was not found.");
            }
            var before = RegularFileIdentity.Read(path);
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var after = RegularFileIdentity.Read(path);
                if (after != before)
                {
                    throw new VaultException(
                        VaultErrorCode.BackupNotFound,
                        "Backup file changed before download.");
                }
                return Task.FromResult(
                    new BackupDownloadLease(
                        fileName,
                        after.Bytes,
                        after,
                        stream));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException)
        {
            throw new VaultException(
                VaultErrorCode.BackupNotFound,
                "Backup file was not found.",
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to open the backup file.",
                exception);
        }
    }

    private static async Task<BackupListEntry?> InspectFileAsync(
        string path,
        string fileName,
        VerifiedBackupCache cache,
        CancellationToken cancellationToken)
    {
        RegularFileIdentity identity;
        try
        {
            identity = RegularFileIdentity.Read(path);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return null;
        }

        try
        {
            var preflight = await BackupZipPreflight.InspectAsync(
                path,
                cancellationToken);
            var manifest = await BackupManifestReader.ReadAsync(
                path,
                preflight,
                cancellationToken);
            var after = RegularFileIdentity.Read(path);
            if (after != identity)
            {
                return InvalidEntry(
                    path,
                    fileName,
                    identity,
                    "restore_archive_invalid");
            }

            var status = cache.TryGet(
                identity.CanonicalPath,
                identity,
                out _)
                ? BackupFileStatus.Verified
                : BackupFileStatus.ManifestReadable;
            var item = new BackupListItem(
                fileName,
                identity.Bytes,
                identity.ModifiedAtUtc,
                manifest.CreatedAtUtc,
                manifest.FormatVersion,
                status,
                null);
            return new BackupListEntry(
                item,
                manifest.CreatedAtUtc);
        }
        catch (BackupPackageValidationException exception)
        {
            return InvalidEntry(
                path,
                fileName,
                identity,
                exception.Code);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or OverflowException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return InvalidEntry(
                path,
                fileName,
                identity,
                "restore_archive_invalid");
        }
    }

    private static BackupListEntry InvalidEntry(
        string path,
        string fileName,
        RegularFileIdentity identity,
        string errorCode) =>
        new(
            new BackupListItem(
                fileName,
                identity.Bytes,
                identity.ModifiedAtUtc,
                null,
                null,
                BackupFileStatus.Invalid,
                errorCode),
            identity.ModifiedAtUtc);

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? BackupConstants.DefaultPageSize;
        if (pageSize is < 1 or > BackupConstants.MaxPageSize)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Backup page size must be between 1 and {BackupConstants.MaxPageSize}.");
        }

        return pageSize;
    }

    private sealed record BackupListEntry(
        BackupListItem Item,
        DateTimeOffset EffectiveCreatedAtUtc);
}
