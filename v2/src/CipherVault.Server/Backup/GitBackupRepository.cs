using System.Globalization;
using System.Security.Cryptography;
using CipherVault.Core.Security;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Backup;

internal sealed record GitSnapshotPublishResult(
    bool Changed,
    string? CommitSha);

internal sealed record GitPushResult(
    bool Succeeded,
    string? ErrorCode);

internal interface IGitBackupRepository
{
    Task<GitRepositoryInspection> InspectAsync(
        GitBackupOptions options,
        CancellationToken cancellationToken);

    Task<GitSnapshotPublishResult> PublishSnapshotAsync(
        GitBackupOptions options,
        string sourceBackupPath,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken);

    Task<GitPushResult> PushAsync(
        GitBackupOptions options,
        CancellationToken cancellationToken);
}

internal sealed class GitBackupRepository : IGitBackupRepository
{
    private const string LatestBackupFileName = "latest.cvbackup";
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(120);
    private readonly IGitCommandRunner runner;

    public GitBackupRepository(IGitCommandRunner runner)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<GitRepositoryInspection> InspectAsync(
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var repository = RequireRepositoryPath(options);
        await EnsureValidRefAsync(
            repository,
            options,
            cancellationToken);
        var workTree = await RunRequiredAsync(
            repository,
            options,
            ["rev-parse", "--is-inside-work-tree"],
            InspectTimeout,
            cancellationToken);
        if (!string.Equals(
                workTree.StandardOutput.Trim(),
                "true",
                StringComparison.Ordinal))
        {
            throw InvalidRepository();
        }

        var topLevel = await RunRequiredAsync(
            repository,
            options,
            ["rev-parse", "--show-toplevel"],
            InspectTimeout,
            cancellationToken);
        var canonicalTopLevel = PathSafety.Canonicalize(
            topLevel.StandardOutput.Trim(),
            "Git worktree root");
        if (!string.Equals(
                canonicalTopLevel,
                repository,
                PathSafety.PathComparison))
        {
            throw InvalidRepository();
        }

        var branchResult = await RunRequiredAsync(
            repository,
            options,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            InspectTimeout,
            cancellationToken);
        var branch = branchResult.StandardOutput.Trim();
        if (!string.Equals(branch, options.Branch, StringComparison.Ordinal))
        {
            throw InvalidRepository();
        }

        await EnsureRemoteExistsAsync(
            repository,
            options,
            cancellationToken);
        _ = await RequireSinglePushUrlAsync(
            repository,
            options,
            cancellationToken);

        var head = await runner.RunAsync(
            repository,
            ["rev-parse", "--verify", "HEAD"],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (head.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }

        var hasUnbornHead = head.ExitCode != 0;
        var headSha = hasUnbornHead ? null : head.StandardOutput.Trim();
        if (headSha is not null && !IsCommitSha(headSha))
        {
            throw InvalidRepository();
        }

        var blockingState = await ReadBlockingStateAsync(
            repository,
            options,
            cancellationToken);

        var ignored = await runner.RunAsync(
            repository,
            ["check-ignore", "-q", LatestBackupFileName],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (ignored.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (ignored.ExitCode == 0)
        {
            throw InvalidRepository();
        }

        var objectBytes = await ReadObjectBytesAsync(
            repository,
            options,
            cancellationToken);
        var firstCommitAtUtc = hasUnbornHead
            ? null
            : await ReadFirstCommitTimeAsync(
                repository,
                options,
                cancellationToken);
        return new GitRepositoryInspection(
            branch,
            headSha,
            hasUnbornHead,
            objectBytes,
            firstCommitAtUtc,
            blockingState.HasMergeOrRebaseState,
            blockingState.HasConflicts);
    }

    public async Task<GitSnapshotPublishResult> PublishSnapshotAsync(
        GitBackupOptions options,
        string sourceBackupPath,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        var repository = RequireRepositoryPath(options);
        await EnsureValidRefAsync(
            repository,
            options,
            cancellationToken);
        await EnsureNoBlockingOperationAsync(
            repository,
            options,
            cancellationToken);
        var source = PathSafety.Canonicalize(
            sourceBackupPath,
            "Source backup");
        PathSafety.EnsureNoLinkComponents(source);
        if (!PathSafety.IsRegularFile(source))
        {
            throw new GitCommandException(GitBackupErrorCodes.SnapshotFailed);
        }

        await EnsureNoCleanFilterAsync(
            repository,
            options,
            cancellationToken);
        var target = Path.Combine(repository, LatestBackupFileName);
        PathSafety.EnsureNoLinkComponents(target);
        await CopyVerifiedAsync(source, target, cancellationToken);
        await EnsureNoBlockingOperationAsync(
            repository,
            options,
            cancellationToken);

        var hasHead = await HasHeadAsync(
            repository,
            options,
            cancellationToken);
        var indexDirectory = CreateTemporaryIndexDirectory();
        var indexPath = Path.Combine(indexDirectory, "index");
        try
        {
            await RunRequiredAsync(
                repository,
                options,
                hasHead
                    ? ["read-tree", "HEAD"]
                    : ["read-tree", "--empty"],
                InspectTimeout,
                cancellationToken,
                GitBackupErrorCodes.CommitFailed,
                indexPath);
            var blob = await RunRequiredAsync(
                repository,
                options,
                ["hash-object", "-w", "--no-filters", "--", LatestBackupFileName],
                CommitTimeout,
                cancellationToken,
                GitBackupErrorCodes.CommitFailed,
                indexPath);
            var blobSha = blob.StandardOutput.Trim();
            if (!IsCommitSha(blobSha))
            {
                throw new GitCommandException(GitBackupErrorCodes.CommitFailed);
            }

            await RunRequiredAsync(
                repository,
                options,
                [
                    "update-index",
                    "--add",
                    "--cacheinfo",
                    $"100644,{blobSha},{LatestBackupFileName}"
                ],
                InspectTimeout,
                cancellationToken,
                GitBackupErrorCodes.CommitFailed,
                indexPath);

            var diff = await runner.RunAsync(
                repository,
                [
                    "diff",
                    "--cached",
                    "--quiet",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--",
                    LatestBackupFileName
                ],
                CreateEnvironment(options, indexPath),
                InspectTimeout,
                cancellationToken);
            if (diff.TimedOut)
            {
                throw new GitCommandException(GitBackupErrorCodes.Timeout);
            }
            if (diff.ExitCode == 0)
            {
                return new GitSnapshotPublishResult(
                    Changed: false,
                    CommitSha: null);
            }
            if (diff.ExitCode != 1)
            {
                throw new GitCommandException(GitBackupErrorCodes.CommitFailed);
            }

            var message = $"backup: {committedAtUtc.ToUniversalTime():O}";
            await RunRequiredAsync(
                repository,
                options,
                [
                    "commit",
                    "--no-verify",
                    "--no-gpg-sign",
                    "-m",
                    message
                ],
                CommitTimeout,
                cancellationToken,
                GitBackupErrorCodes.CommitFailed,
                indexPath);
            var head = await RunRequiredAsync(
                repository,
                options,
                ["rev-parse", "HEAD"],
                InspectTimeout,
                cancellationToken,
                GitBackupErrorCodes.CommitFailed);
            var commitSha = head.StandardOutput.Trim();
            if (!IsCommitSha(commitSha))
            {
                throw new GitCommandException(GitBackupErrorCodes.CommitFailed);
            }

            return new GitSnapshotPublishResult(
                Changed: true,
                CommitSha: commitSha);
        }
        finally
        {
            DeleteIndexDirectoryBestEffort(indexDirectory);
        }
    }

    public async Task<GitPushResult> PushAsync(
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var repository = RequireRepositoryPath(options);
        await EnsureValidRefAsync(
            repository,
            options,
            cancellationToken);
        var pushUrl = await RequireSinglePushUrlAsync(
            repository,
            options,
            cancellationToken);
        var result = await runner.RunAsync(
            repository,
            [
                "push",
                "--porcelain",
                "--no-verify",
                "--no-follow-tags",
                "--no-signed",
                "--no-recurse-submodules",
                "--",
                pushUrl,
                $"refs/heads/{options.Branch}:refs/heads/{options.Branch}"
            ],
            CreateEnvironment(options),
            PushTimeout,
            cancellationToken);
        if (result.TimedOut)
        {
            return new GitPushResult(
                Succeeded: false,
                GitBackupErrorCodes.Timeout);
        }
        if (result.ExitCode == 0)
        {
            return new GitPushResult(Succeeded: true, ErrorCode: null);
        }

        var error = $"{result.StandardOutput}\n{result.StandardError}";
        var code = ContainsAny(
            error,
            "Authentication failed",
            "terminal prompts disabled",
            "could not read Username",
            "Permission denied",
            "publickey")
            ? GitBackupErrorCodes.AuthenticationFailed
            : ContainsAny(
                error,
                "non-fast-forward",
                "fetch first",
                "[rejected]")
                ? GitBackupErrorCodes.NonFastForward
                : GitBackupErrorCodes.PushFailed;
        return new GitPushResult(Succeeded: false, code);
    }

    private async Task EnsureValidRefAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repository,
            ["check-ref-format", $"refs/heads/{options.Branch}"],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (result.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (result.ExitCode != 0)
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid);
        }
    }

    private async Task EnsureNoCleanFilterAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repository,
            ["check-attr", "-z", "filter", "--", LatestBackupFileName],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (result.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (result.ExitCode != 0)
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid);
        }

        var fields = result.StandardOutput.Split(
            '\0',
            StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0 || fields.Length % 3 != 0)
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid);
        }

        for (var index = 0; index < fields.Length; index += 3)
        {
            if (!string.Equals(
                    fields[index + 2],
                    "unspecified",
                    StringComparison.Ordinal))
            {
                throw new GitCommandException(
                    GitBackupErrorCodes.ConfigurationInvalid);
            }
        }
    }

    private async Task EnsureRemoteExistsAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync(
            repository,
            options,
            ["remote"],
            InspectTimeout,
            cancellationToken);
        var remotes = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToArray();
        if (!remotes.Contains(options.Remote, StringComparer.Ordinal))
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid);
        }
    }

    private async Task<string> RequireSinglePushUrlAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync(
            repository,
            options,
            [
                "remote",
                "get-url",
                "--push",
                "--all",
                options.Remote
            ],
            InspectTimeout,
            cancellationToken,
            GitBackupErrorCodes.ConfigurationInvalid);
        var urls = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToArray();
        if (urls.Length != 1
            || !IsAllowedPushUrl(urls[0], options))
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid);
        }

        return urls[0];
    }

    private static bool IsAllowedPushUrl(
        string value,
        GitBackupOptions options)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
        {
            return false;
        }

        if (Path.IsPathFullyQualified(value))
        {
            return options.AllowFileRemote;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Scheme.ToLowerInvariant() switch
            {
                "https" or "ssh" => true,
                "http" => options.AllowHttp,
                "git" => options.AllowGitProtocol,
                "file" => options.AllowFileRemote,
                _ => false
            };
        }

        return !value.Contains("::", StringComparison.Ordinal)
            && value.Contains('@', StringComparison.Ordinal)
            && value.Contains(':', StringComparison.Ordinal)
            && !value.Any(char.IsWhiteSpace);
    }

    private async Task<bool> HasHeadAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repository,
            ["rev-parse", "--verify", "HEAD"],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (result.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        return result.ExitCode == 0;
    }

    private async Task<long> ReadObjectBytesAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync(
            repository,
            options,
            ["count-objects", "-v"],
            InspectTimeout,
            cancellationToken);
        long total = 0;
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator < 0
                || !long.TryParse(
                    line[(separator + 1)..].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var kibibytes))
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key is "size" or "size-pack")
            {
                total = checked(total + (kibibytes * 1024));
            }
        }

        return total;
    }

    private async Task<DateTimeOffset?> ReadFirstCommitTimeAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repository,
            [
                "log",
                "--reverse",
                "--format=%cI",
                $"refs/heads/{options.Branch}",
                "--max-count=1"
            ],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (result.ExitCode != 0
            || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        return DateTimeOffset.Parse(
            result.StandardOutput.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    private async Task<bool> HasAnyReferenceAsync(
        string repository,
        GitBackupOptions options,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references)
        {
            var result = await runner.RunAsync(
                repository,
                ["rev-parse", "-q", "--verify", reference],
                CreateEnvironment(options),
                InspectTimeout,
                cancellationToken);
            if (result.TimedOut)
            {
                throw new GitCommandException(GitBackupErrorCodes.Timeout);
            }
            if (result.ExitCode == 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<GitBlockingState> ReadBlockingStateAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var mergeState = await HasAnyReferenceAsync(
            repository,
            options,
            ["MERGE_HEAD", "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD"],
            cancellationToken)
            || await HasGitPathDirectoryAsync(
                repository,
                options,
                "rebase-merge",
                cancellationToken)
            || await HasGitPathDirectoryAsync(
                repository,
                options,
                "rebase-apply",
                cancellationToken);
        var conflicts = await runner.RunAsync(
            repository,
            [
                "diff",
                "--name-only",
                "--diff-filter=U",
                "--no-ext-diff",
                "--no-textconv",
                "-z"
            ],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (conflicts.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (conflicts.ExitCode != 0)
        {
            throw new GitCommandException(GitBackupErrorCodes.RepositoryInvalid);
        }

        return new GitBlockingState(
            mergeState,
            conflicts.StandardOutput.Length > 0);
    }

    private async Task EnsureNoBlockingOperationAsync(
        string repository,
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var state = await ReadBlockingStateAsync(
            repository,
            options,
            cancellationToken);
        if (state.HasMergeOrRebaseState || state.HasConflicts)
        {
            throw new GitCommandException(GitBackupErrorCodes.RepositoryBusy);
        }
    }

    private async Task<bool> HasGitPathDirectoryAsync(
        string repository,
        GitBackupOptions options,
        string gitPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repository,
            ["rev-parse", "--git-path", gitPath],
            CreateEnvironment(options),
            InspectTimeout,
            cancellationToken);
        if (result.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (result.ExitCode != 0
            || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return false;
        }

        var path = result.StandardOutput.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            path = Path.Combine(repository, path);
        }

        return Directory.Exists(path);
    }

    private async Task<GitCommandResult> RunRequiredAsync(
        string repository,
        GitBackupOptions options,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? errorCode = null,
        string? temporaryIndexPath = null)
    {
        var result = await runner.RunAsync(
            repository,
            arguments,
            CreateEnvironment(options, temporaryIndexPath),
            timeout,
            cancellationToken);
        if (result.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (result.ExitCode != 0)
        {
            throw new GitCommandException(
                errorCode ?? GitBackupErrorCodes.RepositoryInvalid);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> CreateEnvironment(
        GitBackupOptions options,
        string? temporaryIndexPath = null) =>
        GitExecutionEnvironment.CreateEnvironment(
            options,
            temporaryIndexPath);

    private static string RequireRepositoryPath(GitBackupOptions options)
    {
        if (!options.Enabled
            || options.ConfigurationErrorCode is not null
            || string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid);
        }

        GitExecutionEnvironment.ValidateConfiguration(options);
        var repository = PathSafety.Canonicalize(
            options.RepositoryPath,
            "Git backup repository");
        if (!Directory.Exists(repository))
        {
            throw InvalidRepository();
        }

        PathSafety.EnsureNoLinkComponents(repository);
        return repository;
    }

    private static string CreateTemporaryIndexDirectory()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-git-index-{Guid.NewGuid():N}");
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Directory.CreateDirectory(path);
                VaultFilePermissions.ProtectDirectory(path);
                return path;
            }
        }

        throw new GitCommandException(GitBackupErrorCodes.CommitFailed);
    }

    private static void DeleteIndexDirectoryBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // A unique temporary index is never used as a Git backup source.
        }
    }

    private static async Task CopyVerifiedAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new GitCommandException(GitBackupErrorCodes.CommitFailed);
        var temp = Path.Combine(
            directory,
            $".{LatestBackupFileName}.{Guid.NewGuid():N}.tmp");
        var sourceHash = string.Empty;
        try
        {
            await using (var input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                VaultFilePermissions.ProtectFile(temp);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                sourceHash = Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant();
            }

            if (File.Exists(target))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.Replace(temp, target, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temp, target, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, target);
            }

            var targetHash = await HashFileAsync(target, cancellationToken);
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
            {
                throw new GitCommandException(GitBackupErrorCodes.SnapshotFailed);
            }
        }
        catch (GitCommandException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new GitCommandException(
                GitBackupErrorCodes.SnapshotFailed,
                exception);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException)
                {
                    // Preserve the original failure.
                }
            }
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool ContainsAny(
        string value,
        params string[] candidates) =>
        candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsCommitSha(string value) =>
        value.Length is 40 or 64
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static GitCommandException InvalidRepository() =>
        new(GitBackupErrorCodes.RepositoryInvalid);

    private sealed record GitBlockingState(
        bool HasMergeOrRebaseState,
        bool HasConflicts);
}
