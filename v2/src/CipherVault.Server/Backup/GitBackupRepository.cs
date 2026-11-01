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
        var workTree = await RunRequiredAsync(
            repository,
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
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            InspectTimeout,
            cancellationToken);
        var branch = branchResult.StandardOutput.Trim();
        if (!string.Equals(branch, options.Branch, StringComparison.Ordinal))
        {
            throw InvalidRepository();
        }

        await RunRequiredAsync(
            repository,
            ["remote", "get-url", options.Remote],
            InspectTimeout,
            cancellationToken);

        var head = await runner.RunAsync(
            repository,
            ["rev-parse", "--verify", "HEAD"],
            environment: null,
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

        var mergeState = await HasAnyReferenceAsync(
            repository,
            ["MERGE_HEAD", "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD"],
            cancellationToken)
            || await HasGitPathDirectoryAsync(
                repository,
                "rebase-merge",
                cancellationToken)
            || await HasGitPathDirectoryAsync(
                repository,
                "rebase-apply",
                cancellationToken);
        var conflicts = await runner.RunAsync(
            repository,
            ["diff", "--name-only", "--diff-filter=U", "-z"],
            environment: null,
            InspectTimeout,
            cancellationToken);
        if (conflicts.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }

        var ignored = await runner.RunAsync(
            repository,
            ["check-ignore", "-q", LatestBackupFileName],
            environment: null,
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
            cancellationToken);
        var firstCommitAtUtc = hasUnbornHead
            ? null
            : await ReadFirstCommitTimeAsync(
                repository,
                options.Branch,
                cancellationToken);
        return new GitRepositoryInspection(
            branch,
            headSha,
            hasUnbornHead,
            objectBytes,
            firstCommitAtUtc,
            mergeState,
            conflicts.StandardOutput.Length > 0);
    }

    public async Task<GitSnapshotPublishResult> PublishSnapshotAsync(
        GitBackupOptions options,
        string sourceBackupPath,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        var repository = RequireRepositoryPath(options);
        var source = PathSafety.Canonicalize(
            sourceBackupPath,
            "Source backup");
        PathSafety.EnsureNoLinkComponents(source);
        if (!PathSafety.IsRegularFile(source))
        {
            throw new GitCommandException(GitBackupErrorCodes.SnapshotFailed);
        }

        var target = Path.Combine(repository, LatestBackupFileName);
        PathSafety.EnsureNoLinkComponents(target);
        await CopyVerifiedAsync(source, target, cancellationToken);

        await RunRequiredAsync(
            repository,
            ["add", "--", LatestBackupFileName],
            InspectTimeout,
            cancellationToken);
        var diff = await runner.RunAsync(
            repository,
            ["diff", "--cached", "--quiet", "--", LatestBackupFileName],
            environment: null,
            InspectTimeout,
            cancellationToken);
        if (diff.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (diff.ExitCode == 0)
        {
            return new GitSnapshotPublishResult(Changed: false, CommitSha: null);
        }
        if (diff.ExitCode != 1)
        {
            throw new GitCommandException(GitBackupErrorCodes.CommitFailed);
        }

        var message = $"backup: {committedAtUtc.ToUniversalTime():O}";
        await RunRequiredAsync(
            repository,
            [
                "-c", "user.name=CipherVault Backup",
                "-c", "user.email=cipher-vault@localhost",
                "commit",
                "--only",
                "--no-verify",
                "-m", message,
                "--", LatestBackupFileName
            ],
            CommitTimeout,
            cancellationToken,
            GitBackupErrorCodes.CommitFailed);
        var head = await RunRequiredAsync(
            repository,
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

    public async Task<GitPushResult> PushAsync(
        GitBackupOptions options,
        CancellationToken cancellationToken)
    {
        var repository = RequireRepositoryPath(options);
        var result = await runner.RunAsync(
            repository,
            ["push", options.Remote, options.Branch],
            environment: null,
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

        var error = result.StandardError;
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

    private async Task<long> ReadObjectBytesAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync(
            repository,
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
        string branch,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repository,
            ["log", "--reverse", "--format=%cI", branch, "--max-count=1"],
            environment: null,
            InspectTimeout,
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
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
        IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references)
        {
            var result = await runner.RunAsync(
                repository,
                ["rev-parse", "-q", "--verify", reference],
                environment: null,
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

    private async Task<bool> HasGitPathDirectoryAsync(
        string repository,
        string gitPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repository,
            ["rev-parse", "--git-path", gitPath],
            environment: null,
            InspectTimeout,
            cancellationToken);
        if (result.TimedOut)
        {
            throw new GitCommandException(GitBackupErrorCodes.Timeout);
        }
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
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
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? errorCode = null)
    {
        var result = await runner.RunAsync(
            repository,
            arguments,
            environment: null,
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

    private static string RequireRepositoryPath(GitBackupOptions options)
    {
        if (!options.Enabled
            || options.ConfigurationErrorCode is not null
            || string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            throw new GitCommandException(GitBackupErrorCodes.ConfigurationInvalid);
        }

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
}
