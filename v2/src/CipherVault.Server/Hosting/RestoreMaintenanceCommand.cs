using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CipherVault.Core.Backup;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Server.Hosting;

public enum RestoreFailureCode
{
    ArgumentsInvalid,
    ServerRunning,
    NotInteractiveTerminal,
    TargetInvalid,
    ArchiveInvalid,
    ManifestInvalid,
    HashMismatch,
    UnlockFailed,
    DatabaseInvalid,
    SchemaUnsupported,
    PublishFailed,
    Cancelled
}

public sealed record RestoreOutcome(
    bool Succeeded,
    int ExitCode,
    RestoreFailureCode? FailureCode,
    string SafeMessage);

public static class RestoreExitCodes
{
    public const int Success = 0;
    public const int ArgumentsInvalid = 2;
    public const int ServerRunning = 3;
    public const int NotInteractiveTerminal = 4;
    public const int TargetInvalid = 5;
    public const int ArchiveInvalid = 6;
    public const int UnlockFailed = 7;
    public const int DatabaseInvalid = 8;
    public const int PublishFailed = 9;
}

public sealed class RestoreMaintenanceCommand
{
    private const string MarkerFileName =
        ".cipher-vault-restore-marker.json";
    private const string MarkerFormat =
        "cipher-vault-restore-staging";
    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public async Task<int> RunAsync(
        ServerOptions options,
        RestoreCommandOptions command,
        IInteractiveConsole terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(terminal);
        string? stagingDirectory = null;
        try
        {
            var packagePath = ValidatePackage(command.BackupFile);
            var target = ValidateTarget(
                command.TargetDirectory,
                options.Paths);
            if (!terminal.IsInteractive)
            {
                return Fail(
                    terminal,
                    RestoreFailureCode.NotInteractiveTerminal,
                    RestoreExitCodes.NotInteractiveTerminal,
                    "restore_not_tty");
            }

            Directory.CreateDirectory(options.Paths.ConfigDirectory);
            VaultFilePermissions.ProtectDirectory(
                options.Paths.ConfigDirectory);
            PathSafety.EnsureNoLinkComponents(
                options.Paths.ConfigDirectory);
            try
            {
                await using var instanceLock = ServerInstanceLock.Acquire(
                    options.Paths.ServerLockPath);
                return await RunLockedAsync(
                    packagePath,
                    target,
                    terminal,
                    cancellationToken,
                    staging =>
                    {
                        stagingDirectory = staging;
                    });
            }
            catch (ServerStartupException exception)
                when (exception.Code == ServerStartupCode.InstanceConflict)
            {
                return Fail(
                    terminal,
                    RestoreFailureCode.ServerRunning,
                    RestoreExitCodes.ServerRunning,
                    "restore_server_running");
            }
        }
        catch (OperationCanceledException)
        {
            return Fail(
                terminal,
                RestoreFailureCode.Cancelled,
                RestoreExitCodes.PublishFailed,
                "restore_cancelled");
        }
        catch (RestoreCommandException exception)
        {
            return Fail(
                terminal,
                exception.FailureCode,
                exception.ExitCode,
                exception.SafeMessage);
        }
        catch (Exception exception) when (
            exception is VaultException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ServerStartupException)
        {
            return Fail(
                terminal,
                RestoreFailureCode.TargetInvalid,
                RestoreExitCodes.TargetInvalid,
                "restore_target_invalid");
        }
        finally
        {
            DeleteDirectoryBestEffort(stagingDirectory);
        }
    }

    private static async Task<int> RunLockedAsync(
        string packagePath,
        string target,
        IInteractiveConsole terminal,
        CancellationToken cancellationToken,
        Action<string> setStagingDirectory)
    {
        var targetHash = RestoreTargetLock.GetTargetHash(target);
        await using var targetLock = AcquireTargetLock(target);
        CleanupStaleStaging(
            Path.GetDirectoryName(target)!,
            targetHash);
        var password = await terminal.ReadPasswordAsync(cancellationToken);
        if (password is null)
        {
            throw new RestoreCommandException(
                RestoreFailureCode.NotInteractiveTerminal,
                RestoreExitCodes.NotInteractiveTerminal,
                "restore_not_tty");
        }

        var staging = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".cipher-vault-restore-{targetHash}-{Guid.NewGuid():N}.partial");
        setStagingDirectory(staging);
        Directory.CreateDirectory(staging);
        VaultFilePermissions.ProtectDirectory(staging);
        await WriteMarkerAsync(
            staging,
            targetHash,
            cancellationToken);
        var payload = Path.Combine(staging, "payload");
        Directory.CreateDirectory(payload);
        VaultFilePermissions.ProtectDirectory(payload);
        var credential = new InteractivePasswordCredential(password);
        RestoreVerificationResult verified;
        try
        {
            verified = await BackupRestoreStore.ExtractAndValidateAsync(
                packagePath,
                payload,
                credential,
                cancellationToken);
        }
        catch (BackupPackageValidationException exception)
        {
            var mapped = MapValidationFailure(exception.Code);
            throw new RestoreCommandException(
                mapped.Code,
                mapped.ExitCode,
                mapped.SafeMessage,
                exception);
        }
        catch (VaultException exception)
            when (exception.Code == VaultErrorCode.InvalidCredential)
        {
            throw new RestoreCommandException(
                RestoreFailureCode.UnlockFailed,
                RestoreExitCodes.UnlockFailed,
                "restore_unlock_failed",
                exception);
        }
        catch (Exception exception) when (
            exception is VaultException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new RestoreCommandException(
                RestoreFailureCode.DatabaseInvalid,
                RestoreExitCodes.DatabaseInvalid,
                "restore_database_invalid",
                exception);
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new RestoreCommandException(
                RestoreFailureCode.TargetInvalid,
                RestoreExitCodes.TargetInvalid,
                "restore_target_invalid");
        }

        try
        {
            Directory.Move(payload, target);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new RestoreCommandException(
                RestoreFailureCode.PublishFailed,
                RestoreExitCodes.PublishFailed,
                "restore_publish_failed",
                exception);
        }

        terminal.WriteSafeSuccess(verified);
        return RestoreExitCodes.Success;
    }

    private static string ValidatePackage(string packagePath)
    {
        try
        {
            var path = PathSafety.Canonicalize(
                packagePath,
                "Restore package");
            PathSafety.EnsureNoLinkComponents(path);
            if (!PathSafety.IsRegularFile(path))
            {
                throw new RestoreCommandException(
                    RestoreFailureCode.ArchiveInvalid,
                    RestoreExitCodes.ArchiveInvalid,
                    "restore_archive_invalid");
            }
            if (new FileInfo(path).Length is <= 0 or > BackupConstants.MaxPackageBytes)
            {
                throw new RestoreCommandException(
                    RestoreFailureCode.ArchiveInvalid,
                    RestoreExitCodes.ArchiveInvalid,
                    "restore_archive_invalid");
            }
            return path;
        }
        catch (RestoreCommandException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is VaultException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new RestoreCommandException(
                RestoreFailureCode.ArchiveInvalid,
                RestoreExitCodes.ArchiveInvalid,
                "restore_archive_invalid",
                exception);
        }
    }

    private static string ValidateTarget(
        string targetPath,
        ApplicationPaths paths)
    {
        try
        {
            var target = PathSafety.Canonicalize(
                targetPath,
                "Restore target");
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw InvalidTarget();
            }
            var parent = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(parent)
                || !Directory.Exists(parent))
            {
                throw InvalidTarget();
            }
            PathSafety.EnsureNoLinkComponents(parent);
            PathSafety.EnsureNoLinkComponents(target);
            foreach (var protectedPath in new[]
                     {
                         paths.VaultDirectory,
                         paths.ConfigDirectory,
                         paths.BackupDirectory
                     })
            {
                var relation = PathSafety.GetRelation(
                    target,
                    protectedPath);
                if (relation.IsEqual
                    || relation.IsChildOf
                    || relation.IsAncestorOf)
                {
                    throw InvalidTarget();
                }
            }
            return target;
        }
        catch (RestoreCommandException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is VaultException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new RestoreCommandException(
                RestoreFailureCode.TargetInvalid,
                RestoreExitCodes.TargetInvalid,
                "restore_target_invalid",
                exception);
        }
    }

    private static RestoreTargetLock AcquireTargetLock(string target)
    {
        try
        {
            return RestoreTargetLock.Acquire(target);
        }
        catch (VaultException exception)
        {
            throw new RestoreCommandException(
                RestoreFailureCode.TargetInvalid,
                RestoreExitCodes.TargetInvalid,
                "restore_target_invalid",
                exception);
        }
    }

    private static void CleanupStaleStaging(
        string parent,
        string targetHash)
    {
        var pattern = new Regex(
            $"^\\.cipher-vault-restore-{targetHash}-[0-9a-f]{{32}}\\.partial$",
            RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     parent,
                     $".cipher-vault-restore-{targetHash}-*.partial",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!pattern.IsMatch(name))
            {
                continue;
            }
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidTarget();
            }
            EnsureNoLinks(path);
            ValidateStagingMarker(path, targetHash);
            ValidateStagingContents(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static void ValidateStagingMarker(
        string directory,
        string targetHash)
    {
        var markerPath = Path.Combine(directory, MarkerFileName);
        if (!PathSafety.IsRegularFile(markerPath))
        {
            throw InvalidTarget();
        }
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllBytes(markerPath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidTarget();
            }
            var propertyNames = root.EnumerateObject()
                .Select(property => property.Name)
                .ToArray();
            if (propertyNames.Length != 3
                || propertyNames.Distinct(StringComparer.Ordinal).Count() != 3
                || !propertyNames.ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["format", "version", "targetNameHash"])
                || root.GetProperty("format").GetString()
                    != MarkerFormat
                || root.GetProperty("version").GetInt32() != 1
                || root.GetProperty("targetNameHash").GetString()
                    != targetHash)
            {
                throw InvalidTarget();
            }
        }
        catch (RestoreCommandException)
        {
            throw;
        }
        catch
        {
            throw InvalidTarget();
        }
    }

    private static void ValidateStagingContents(string directory)
    {
        var names = Directory.EnumerateFileSystemEntries(
                directory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (names.Any(name =>
                name is not (MarkerFileName or "payload"))
            || !names.Contains(MarkerFileName, StringComparer.Ordinal))
        {
            throw InvalidTarget();
        }
        if (names.Contains("payload", StringComparer.Ordinal))
        {
            var payloadPath = Path.Combine(directory, "payload");
            var attributes = File.GetAttributes(payloadPath);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidTarget();
            }
            EnsureNoLinks(payloadPath);
        }
    }

    private static async Task WriteMarkerAsync(
        string staging,
        string targetHash,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                format = MarkerFormat,
                version = 1,
                targetNameHash = targetHash
            },
            MarkerJson);
        var path = Path.Combine(staging, MarkerFileName);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        VaultFilePermissions.ProtectFile(path);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureNoLinks(string directory)
    {
        PathSafety.EnsureNoLinkComponents(directory);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw InvalidTarget();
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
            }
        }
    }

    private static (RestoreFailureCode Code, int ExitCode, string SafeMessage)
        MapValidationFailure(string code) =>
        code switch
        {
            "restore_hash_mismatch" => (
                RestoreFailureCode.HashMismatch,
                RestoreExitCodes.ArchiveInvalid,
                "restore_hash_mismatch"),
            "restore_manifest_invalid" => (
                RestoreFailureCode.ManifestInvalid,
                RestoreExitCodes.ArchiveInvalid,
                "restore_manifest_invalid"),
            "restore_unlock_failed" => (
                RestoreFailureCode.UnlockFailed,
                RestoreExitCodes.UnlockFailed,
                "restore_unlock_failed"),
            "restore_schema_unsupported" => (
                RestoreFailureCode.SchemaUnsupported,
                RestoreExitCodes.DatabaseInvalid,
                "restore_schema_unsupported"),
            "restore_database_invalid" => (
                RestoreFailureCode.DatabaseInvalid,
                RestoreExitCodes.DatabaseInvalid,
                "restore_database_invalid"),
            _ => (
                RestoreFailureCode.ArchiveInvalid,
                RestoreExitCodes.ArchiveInvalid,
                "restore_archive_invalid")
        };

    private static int Fail(
        IInteractiveConsole terminal,
        RestoreFailureCode code,
        int exitCode,
        string safeMessage)
    {
        terminal.WriteSafeError(
            new RestoreOutcome(
                Succeeded: false,
                ExitCode: exitCode,
                FailureCode: code,
                SafeMessage: safeMessage));
        return exitCode;
    }

    private static RestoreCommandException InvalidTarget() =>
        new(
            RestoreFailureCode.TargetInvalid,
            RestoreExitCodes.TargetInvalid,
            "restore_target_invalid");

    private static void DeleteDirectoryBestEffort(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
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
            // A strict .partial directory is never treated as a restored Vault.
        }
    }

    private sealed class RestoreCommandException : Exception
    {
        public RestoreCommandException(
            RestoreFailureCode failureCode,
            int exitCode,
            string safeMessage,
            Exception? innerException = null)
            : base(safeMessage, innerException)
        {
            FailureCode = failureCode;
            ExitCode = exitCode;
            SafeMessage = safeMessage;
        }

        public RestoreFailureCode FailureCode { get; }

        public int ExitCode { get; }

        public string SafeMessage { get; }
    }
}
