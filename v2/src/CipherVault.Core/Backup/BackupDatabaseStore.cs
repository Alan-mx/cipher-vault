using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CipherVault.Core.Backup;

internal enum BackupOperationSignal
{
    Initialized,
    StepCompleted,
    BackupFinishInvoked,
    BackupFinished
}

internal delegate ValueTask BackupOperationObserver(
    BackupOperationSignal signal,
    int code);

public static class BackupDatabaseStore
{
    private const string OwnershipMarkerName =
        ".cipher-vault-backup-owner";
    private const int PagesPerStep = 16;
    private const int RetryDelayMilliseconds = 25;
    private static readonly TimeSpan MaxNoProgressRetry =
        TimeSpan.FromSeconds(5);

    public static Task CreateEncryptedSnapshotAsync(
        VaultOperationContext context,
        string workingDirectory,
        ReadOnlyMemory<byte> databaseKey,
        CancellationToken cancellationToken) =>
        CreateEncryptedSnapshotAsync(
            context,
            workingDirectory,
            databaseKey,
            observer: null,
            cancellationToken);

    internal static async Task CreateEncryptedSnapshotAsync(
        VaultOperationContext context,
        string workingDirectory,
        ReadOnlyMemory<byte> databaseKey,
        BackupOperationObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var attempt = new SnapshotAttempt();
        try
        {
            await CreateSnapshotCoreAsync(
                attempt,
                context,
                workingDirectory,
                databaseKey,
                observer,
                cancellationToken);
        }
        catch (Exception exception)
        {
            attempt.OperationException = MapOperationException(exception);
        }

        await CompleteSnapshotAttemptAsync(attempt, observer, cancellationToken);
        ThrowFinalOutcome(attempt);
    }

    internal static void DeleteDatabaseSidecars(string directory)
    {
        var failures = new List<Exception>();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            TryDeleteFile(
                Path.Combine(
                    directory,
                    BackupConstants.DatabaseEntryName + suffix),
                failures);
        }

        if (failures.Count > 0)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to clean encrypted backup sidecar files.",
                new AggregateException(failures));
        }
    }

    private static async Task CreateSnapshotCoreAsync(
        SnapshotAttempt attempt,
        VaultOperationContext context,
        string workingDirectory,
        ReadOnlyMemory<byte> databaseKey,
        BackupOperationObserver? observer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        attempt.Directory = ValidateWorkingDirectory(workingDirectory);
        attempt.DatabasePath = Path.Combine(
            attempt.Directory,
            BackupConstants.DatabaseEntryName);

        cancellationToken.ThrowIfCancellationRequested();
        attempt.MarkerStream = AcquireOwnershipMarker(
            attempt.Directory,
            out var markerPath);
        attempt.OwnershipAcquired = true;
        attempt.MarkerPath = markerPath;

        EnsureTargetFilesAreAbsent(attempt.Directory);
        attempt.DatabaseOwnedByThisCall = true;

        cancellationToken.ThrowIfCancellationRequested();
        attempt.Destination =
            await SqliteMcDatabaseFactory.OpenConnectionAsync(
                new DatabaseOpenRequest(
                    attempt.Directory,
                    databaseKey,
                    CreateIfMissing: true),
                cancellationToken);
        SqliteMcDatabaseFactory.ExecuteNonQuery(
            attempt.Destination,
            "PRAGMA busy_timeout = 250;");

        cancellationToken.ThrowIfCancellationRequested();
        if (context.Connection.State != System.Data.ConnectionState.Open
            || attempt.Destination.State != System.Data.ConnectionState.Open)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Backup source or destination database is not open.");
        }

        if (context.Connection.Handle == attempt.Destination.Handle)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Backup source and destination connections must be different.");
        }

        if (raw.sqlite3_get_autocommit(attempt.Destination.Handle) != 1)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Backup destination has an active transaction.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        attempt.Backup = raw.sqlite3_backup_init(
            attempt.Destination.Handle,
            "main",
            context.Connection.Handle,
            "main");
        if (attempt.Backup is null)
        {
            var code = raw.sqlite3_errcode(attempt.Destination.Handle);
            throw CreateSqliteOperationException(
                "Unable to initialize the encrypted backup snapshot.",
                code);
        }

        attempt.Initialized = true;
        await NotifySafelyAsync(
            attempt,
            observer,
            BackupOperationSignal.Initialized,
            raw.SQLITE_OK);

        long? noProgressStartedAt = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = raw.sqlite3_backup_step(
                attempt.Backup,
                PagesPerStep);
            await NotifySafelyAsync(
                attempt,
                observer,
                BackupOperationSignal.StepCompleted,
                code);

            var primaryCode = code & 0xFF;
            if (primaryCode == raw.SQLITE_DONE)
            {
                attempt.Completed = true;
                break;
            }

            if (primaryCode == raw.SQLITE_OK)
            {
                noProgressStartedAt = null;
                await Task.Yield();
                continue;
            }

            if (primaryCode is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
            {
                noProgressStartedAt ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(noProgressStartedAt.Value)
                    >= MaxNoProgressRetry)
                {
                    throw CreateSqliteOperationException(
                        "Encrypted backup snapshot remained busy or locked.",
                        code);
                }

                await Task.Delay(
                    RetryDelayMilliseconds,
                    cancellationToken);
                continue;
            }

            if (primaryCode == raw.SQLITE_INTERRUPT)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw CreateSqliteOperationException(
                "Unable to copy the encrypted database snapshot.",
                code);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task CompleteSnapshotAttemptAsync(
        SnapshotAttempt attempt,
        BackupOperationObserver? observer,
        CancellationToken cancellationToken)
    {
        if (attempt.Initialized)
        {
            await NotifySafelyAsync(
                attempt,
                observer,
                BackupOperationSignal.BackupFinishInvoked,
                raw.SQLITE_OK);
            try
            {
                attempt.FinishCode = raw.sqlite3_backup_finish(
                    attempt.Backup!);
            }
            catch (Exception exception)
            {
                attempt.FinishException = exception;
            }
            finally
            {
                await NotifySafelyAsync(
                    attempt,
                    observer,
                    BackupOperationSignal.BackupFinished,
                    attempt.FinishException is null
                        ? attempt.FinishCode
                        : raw.SQLITE_ERROR);
            }
        }

        if (attempt.Destination is not null)
        {
            try
            {
                await attempt.Destination.DisposeAsync();
            }
            catch (Exception exception)
            {
                attempt.DestinationDisposeException = exception;
            }
        }

        if (attempt.MarkerStream is not null)
        {
            try
            {
                await attempt.MarkerStream.DisposeAsync();
            }
            catch (Exception exception)
            {
                attempt.MarkerDisposeException = exception;
            }
        }

        if (attempt.OwnershipAcquired)
        {
            try
            {
                File.Delete(attempt.MarkerPath!);
                attempt.MarkerDeleted = true;
            }
            catch (Exception exception)
            {
                attempt.MarkerDeleteException = exception;
            }
        }

        var completedSuccessfully =
            attempt.OperationException is null
            && attempt.Completed
            && attempt.FinishCode == raw.SQLITE_OK
            && attempt.FinishException is null
            && attempt.DestinationDisposeException is null
            && attempt.MarkerDisposeException is null
            && attempt.MarkerDeleteException is null
            && attempt.ObserverExceptions.Count == 0;

        if (completedSuccessfully)
        {
            try
            {
                DeleteDatabaseSidecars(attempt.Directory!);
                var info = new FileInfo(attempt.DatabasePath!);
                if (!info.Exists || info.Length <= 0)
                {
                    throw new VaultException(
                        VaultErrorCode.DatabaseError,
                        "Encrypted backup snapshot was not created.");
                }
            }
            catch (Exception exception)
            {
                attempt.CleanupExceptions.Add(exception);
                completedSuccessfully = false;
            }
        }

        if (!completedSuccessfully)
        {
            CleanupOwnedSnapshot(attempt);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception)
        {
            attempt.FinalCancellationException = exception;
            CleanupOwnedSnapshot(attempt);
        }
    }

    private static void CleanupOwnedSnapshot(SnapshotAttempt attempt)
    {
        if (attempt.DatabaseOwnedByThisCall)
        {
            TryDeleteTrackedFile(attempt.DatabasePath!, attempt);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                TryDeleteTrackedFile(
                    attempt.DatabasePath + suffix,
                    attempt);
            }
        }

        if (attempt.OwnershipAcquired && !attempt.MarkerDeleted)
        {
            try
            {
                File.Delete(attempt.MarkerPath!);
                attempt.MarkerDeleted = true;
            }
            catch (Exception exception)
            {
                attempt.CleanupExceptions.Add(exception);
                Trace.TraceError(
                    "Unable to remove the backup ownership marker: {0}",
                    exception);
            }
        }
    }

    private static void TryDeleteTrackedFile(
        string path,
        SnapshotAttempt attempt)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            attempt.CleanupExceptions.Add(exception);
            Trace.TraceError(
                "Unable to remove an owned backup staging file: {0}",
                exception);
        }
    }

    private static void TryDeleteFile(
        string path,
        ICollection<Exception> failures)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowFinalOutcome(SnapshotAttempt attempt)
    {
        if (attempt.OperationException is not null)
        {
            RethrowOriginal(attempt.OperationException);
        }

        if (attempt.FinalCancellationException is not null)
        {
            RethrowOriginal(attempt.FinalCancellationException);
        }

        if (attempt.FinishException is not null)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Unable to finish the encrypted backup snapshot.",
                attempt.FinishException);
        }

        if (!attempt.Completed || attempt.FinishCode != raw.SQLITE_OK)
        {
            throw CreateSqliteOperationException(
                "Encrypted backup snapshot did not complete.",
                attempt.FinishCode);
        }

        if (attempt.DestinationDisposeException is not null)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Unable to close the encrypted backup destination.",
                attempt.DestinationDisposeException);
        }

        if (attempt.ObserverExceptions.Count > 0)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Backup lifecycle observation failed.",
                new AggregateException(attempt.ObserverExceptions));
        }

        if (attempt.MarkerDisposeException is not null
            || attempt.MarkerDeleteException is not null
            || attempt.CleanupExceptions.Count > 0
            || !attempt.MarkerDeleted)
        {
            var failures = new List<Exception>();
            if (attempt.MarkerDisposeException is not null)
            {
                failures.Add(attempt.MarkerDisposeException);
            }

            if (attempt.MarkerDeleteException is not null)
            {
                failures.Add(attempt.MarkerDeleteException);
            }

            failures.AddRange(attempt.CleanupExceptions);
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to finalize encrypted backup staging files.",
                new AggregateException(failures));
        }
    }

    private static string ValidateWorkingDirectory(string workingDirectory)
    {
        try
        {
            var canonical = PathSafety.Canonicalize(
                workingDirectory,
                "Backup working directory");
            var provided = Path.TrimEndingDirectorySeparator(
                workingDirectory);
            if (!string.Equals(
                    canonical,
                    provided,
                    PathSafety.PathComparison)
                || !Directory.Exists(canonical))
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Backup working directory must be an existing canonical directory.");
            }

            PathSafety.EnsureNoLinkComponents(canonical);
            return canonical;
        }
        catch (VaultException exception)
            when (exception.Code is VaultErrorCode.InvalidInput
                or VaultErrorCode.StorageError)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Backup working directory is not available.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Backup working directory is not available.",
                exception);
        }
    }

    private static FileStream AcquireOwnershipMarker(
        string directory,
        out string markerPath)
    {
        markerPath = Path.Combine(directory, OwnershipMarkerName);
        try
        {
            return new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Backup working directory is already owned by another operation.",
                exception);
        }
    }

    private static void EnsureTargetFilesAreAbsent(string directory)
    {
        foreach (var name in new[]
                 {
                     BackupConstants.DatabaseEntryName,
                     BackupConstants.DatabaseEntryName + "-wal",
                     BackupConstants.DatabaseEntryName + "-shm",
                     BackupConstants.DatabaseEntryName + "-journal",
                     BackupConstants.KeyEnvelopeEntryName
                 })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Backup working directory already contains target files.");
            }
        }
    }

    private static async ValueTask NotifySafelyAsync(
        SnapshotAttempt attempt,
        BackupOperationObserver? observer,
        BackupOperationSignal signal,
        int code)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            await observer(signal, code);
        }
        catch (Exception exception)
        {
            attempt.ObserverExceptions.Add(exception);
            Trace.TraceError(
                "Encrypted backup lifecycle observer failed: {0}",
                exception);
        }
    }

    private static Exception MapOperationException(Exception exception) =>
        exception switch
        {
            VaultException => exception,
            OperationCanceledException => exception,
            SqliteException sqliteException =>
                CreateSqliteOperationException(
                    "Unable to create the encrypted backup snapshot.",
                    sqliteException.SqliteErrorCode,
                    sqliteException),
            IOException or UnauthorizedAccessException
                or System.Security.SecurityException =>
                new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to access encrypted backup staging files.",
                    exception),
            _ => new VaultException(
                VaultErrorCode.DatabaseError,
                "Unable to create the encrypted backup snapshot.",
                exception)
        };

    private static VaultException CreateSqliteOperationException(
        string message,
        int code,
        Exception? innerException = null) =>
        new(
            VaultErrorCode.DatabaseError,
            $"{message} SQLite code: {code & 0xFF}, extended: {code}.",
            innerException);

    private static void RethrowOriginal(Exception exception) =>
        ExceptionDispatchInfo.Capture(exception).Throw();

    private sealed class SnapshotAttempt
    {
        internal string? Directory { get; set; }

        internal string? DatabasePath { get; set; }

        internal string? MarkerPath { get; set; }

        internal FileStream? MarkerStream { get; set; }

        internal SqliteConnection? Destination { get; set; }

        internal sqlite3_backup? Backup { get; set; }

        internal bool OwnershipAcquired { get; set; }

        internal bool DatabaseOwnedByThisCall { get; set; }

        internal bool Initialized { get; set; }

        internal bool Completed { get; set; }

        internal bool MarkerDeleted { get; set; }

        internal int FinishCode { get; set; } = raw.SQLITE_OK;

        internal Exception? OperationException { get; set; }

        internal Exception? FinishException { get; set; }

        internal Exception? DestinationDisposeException { get; set; }

        internal Exception? MarkerDisposeException { get; set; }

        internal Exception? MarkerDeleteException { get; set; }

        internal Exception? FinalCancellationException { get; set; }

        internal List<Exception> ObserverExceptions { get; } = [];

        internal List<Exception> CleanupExceptions { get; } = [];
    }
}
