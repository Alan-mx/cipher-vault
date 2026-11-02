using System.Security.Cryptography;
using CipherVault.Server.Backup;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Tests;

public sealed class GitBackupRepositoryTests
{
    [Fact]
    public async Task PublishesOnlyLatestBackupAndPushesToRemote()
    {
        using var fixture = new Fixture();
        await fixture.Git.RunAsync(
            fixture.RemotePath,
            ["init", "--bare"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["init", "-b", "main"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["remote", "add", "origin", fixture.RemotePath],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        var options = fixture.CreateOptions();
        var repository = new GitBackupRepository(fixture.Git);
        var inspection = await repository.InspectAsync(
            options,
            CancellationToken.None);
        Assert.True(inspection.HasUnbornHead);
        Assert.Null(inspection.HeadCommitSha);

        var source = Path.Combine(fixture.Root, "snapshot.cvbackup");
        await File.WriteAllBytesAsync(
            source,
            RandomNumberGenerator.GetBytes(4096));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.RepositoryPath, "do-not-commit.txt"),
            "untracked");

        var published = await repository.PublishSnapshotAsync(
            options,
            source,
            DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
            CancellationToken.None);
        Assert.True(published.Changed);
        Assert.Matches("^[0-9a-f]{40}$", published.CommitSha);

        var tree = await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["ls-tree", "-r", "--name-only", "HEAD"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.Equal("latest.cvbackup", tree.StandardOutput.Trim());

        var push = await repository.PushAsync(
            options,
            CancellationToken.None);
        Assert.True(push.Succeeded);
        var remoteFile = await fixture.Git.RunAsync(
            fixture.RemotePath,
            ["show", "main:latest.cvbackup"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.Equal(0, remoteFile.ExitCode);

        var unchanged = await repository.PublishSnapshotAsync(
            options,
            source,
            DateTimeOffset.Parse("2026-11-01T12:05:00Z"),
            CancellationToken.None);
        Assert.False(unchanged.Changed);
    }

    [Fact]
    public async Task PushRejectsNonFastForwardWithoutForce()
    {
        using var fixture = new Fixture();
        await fixture.InitializeBareRemoteAsync();
        var contributor = Path.Combine(fixture.Root, "contributor");
        Directory.CreateDirectory(contributor);
        await fixture.Git.RunAsync(
            contributor,
            ["init", "-b", "main"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        await fixture.Git.RunAsync(
            contributor,
            ["remote", "add", "origin", fixture.RemotePath],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(contributor, "README.md"),
            "remote change");
        await fixture.Git.RunAsync(
            contributor,
            ["add", "--", "README.md"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        await fixture.Git.RunAsync(
            contributor,
            [
                "-c", "user.name=Remote",
                "-c", "user.email=remote@example.invalid",
                "commit", "-m", "remote"
            ],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        await fixture.Git.RunAsync(
            contributor,
            ["push", "origin", "main"],
            GitExecutionEnvironment.CreateEnvironment(
                fixture.CreateOptions()),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        await fixture.InitializeLocalRepositoryAsync();
        var repository = new GitBackupRepository(fixture.Git);
        var options = fixture.CreateOptions();
        var source = Path.Combine(fixture.Root, "snapshot.cvbackup");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(256));
        var published = await repository.PublishSnapshotAsync(
            options,
            source,
            DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
            CancellationToken.None);
        Assert.True(published.Changed);

        var push = await repository.PushAsync(
            options,
            CancellationToken.None);

        Assert.False(push.Succeeded);
        Assert.Equal(GitBackupErrorCodes.NonFastForward, push.ErrorCode);
    }

    [Fact]
    public async Task PublishRejectsRepositoryWithInProgressGitOperation()
    {
        using var fixture = new Fixture();
        await fixture.InitializeBareRemoteAsync();
        await fixture.InitializeLocalRepositoryAsync();
        Directory.CreateDirectory(
            Path.Combine(fixture.RepositoryPath, ".git", "rebase-merge"));
        var source = Path.Combine(fixture.Root, "snapshot.cvbackup");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(256));
        var repository = new GitBackupRepository(fixture.Git);

        var exception = await Assert.ThrowsAsync<GitCommandException>(
            () => repository.PublishSnapshotAsync(
                fixture.CreateOptions(),
                source,
                DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
                CancellationToken.None));

        Assert.Equal(GitBackupErrorCodes.RepositoryBusy, exception.Code);
        Assert.False(
            File.Exists(Path.Combine(fixture.RepositoryPath, "latest.cvbackup")));
    }

    [Fact]
    public async Task CommitAndPushDoNotExecuteRepositoryHooks()
    {
        using var fixture = new Fixture();
        await fixture.InitializeBareRemoteAsync();
        await fixture.InitializeLocalRepositoryAsync();
        var marker = Path.Combine(fixture.Root, "hook-ran.txt");
        var hooks = Path.Combine(fixture.RepositoryPath, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        foreach (var name in new[]
                 {
                     "pre-commit",
                     "prepare-commit-msg",
                     "commit-msg",
                     "post-commit",
                     "pre-push"
                 })
        {
            var path = Path.Combine(hooks, name);
            await File.WriteAllTextAsync(
                path,
                $"#!/bin/sh\nprintf ran >> '{marker.Replace('\\', '/')}'\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
        }

        var source = Path.Combine(fixture.Root, "snapshot.cvbackup");
        await File.WriteAllBytesAsync(
            source,
            RandomNumberGenerator.GetBytes(256));
        var repository = new GitBackupRepository(fixture.Git);
        var published = await repository.PublishSnapshotAsync(
            fixture.CreateOptions(),
            source,
            DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
            CancellationToken.None);
        Assert.True(published.Changed);
        var push = await repository.PushAsync(
            fixture.CreateOptions(),
            CancellationToken.None);

        Assert.True(push.Succeeded);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task CleanFilterConfigurationFailsBeforeWritingIndex()
    {
        using var fixture = new Fixture();
        await fixture.InitializeBareRemoteAsync();
        await fixture.InitializeLocalRepositoryAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.RepositoryPath, ".gitattributes"),
            "latest.cvbackup filter=cipher\n");
        _ = await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["config", "filter.cipher.clean", "false"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        var source = Path.Combine(fixture.Root, "snapshot.cvbackup");
        await File.WriteAllBytesAsync(
            source,
            RandomNumberGenerator.GetBytes(256));
        var repository = new GitBackupRepository(fixture.Git);

        var exception = await Assert.ThrowsAsync<GitCommandException>(
            () => repository.PublishSnapshotAsync(
                fixture.CreateOptions(),
                source,
                DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
                CancellationToken.None));

        Assert.Equal(GitBackupErrorCodes.ConfigurationInvalid, exception.Code);
        Assert.False(File.Exists(
            Path.Combine(fixture.RepositoryPath, "latest.cvbackup")));
    }

    [Fact]
    public async Task TemporaryIndexDoesNotAlterRealStagedState()
    {
        using var fixture = new Fixture();
        await fixture.InitializeBareRemoteAsync();
        await fixture.InitializeLocalRepositoryAsync();
        var stagedPath = Path.Combine(fixture.RepositoryPath, "real-staged.txt");
        await File.WriteAllTextAsync(stagedPath, "user staged data");
        _ = await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["add", "--", "real-staged.txt"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        var source = Path.Combine(fixture.Root, "snapshot.cvbackup");
        await File.WriteAllBytesAsync(
            source,
            RandomNumberGenerator.GetBytes(256));
        var repository = new GitBackupRepository(fixture.Git);

        var published = await repository.PublishSnapshotAsync(
            fixture.CreateOptions(),
            source,
            DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
            CancellationToken.None);

        Assert.True(published.Changed);
        var tree = await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["ls-tree", "-r", "--name-only", "HEAD"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.Equal("latest.cvbackup", tree.StandardOutput.Trim());
        var realIndex = await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["ls-files", "--stage"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.Contains(
            "real-staged.txt",
            realIndex.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "latest.cvbackup",
            realIndex.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerIgnoresInheritedGitEnvironmentOverrides()
    {
        using var fixture = new Fixture();
        await fixture.InitializeLocalRepositoryAsync();
        var injected = new Dictionary<string, string?>
        {
            ["GIT_DIR"] = Path.Combine(fixture.Root, "wrong-git-dir"),
            ["GIT_WORK_TREE"] = Path.Combine(fixture.Root, "wrong-work-tree"),
            ["GIT_INDEX_FILE"] = Path.Combine(fixture.Root, "wrong-index"),
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_0"] = Path.Combine(fixture.Root, "wrong-hooks"),
            ["GIT_SSH_COMMAND"] = "definitely-not-used",
            ["SSH_AUTH_SOCK"] = Path.Combine(fixture.Root, "ssh-agent.sock")
        };
        var previous = injected.ToDictionary(
            pair => pair.Key,
            pair => Environment.GetEnvironmentVariable(pair.Key));
        try
        {
            foreach (var pair in injected)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            var result = await fixture.Git.RunAsync(
                fixture.RepositoryPath,
                ["rev-parse", "--show-toplevel"],
                environment: null,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                Path.GetFullPath(fixture.RepositoryPath),
                Path.GetFullPath(result.StandardOutput.Trim()));
            Assert.Equal(
                injected["SSH_AUTH_SOCK"],
                GitExecutionEnvironment
                    .CreateBaseEnvironment()["SSH_AUTH_SOCK"]);
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    [Fact]
    public async Task MultiplePushUrlsFailClosed()
    {
        using var fixture = new Fixture();
        await fixture.InitializeBareRemoteAsync();
        await fixture.InitializeLocalRepositoryAsync();
        var secondRemote = Path.Combine(fixture.Root, "second.git");
        Directory.CreateDirectory(secondRemote);
        _ = await fixture.Git.RunAsync(
            secondRemote,
            ["init", "--bare"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        _ = await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["remote", "set-url", "--add", "--push", "origin", fixture.RemotePath],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        _ = await fixture.Git.RunAsync(
            fixture.RepositoryPath,
            ["remote", "set-url", "--add", "--push", "origin", secondRemote],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        var repository = new GitBackupRepository(fixture.Git);

        var exception = await Assert.ThrowsAsync<GitCommandException>(
            () => repository.PushAsync(
                fixture.CreateOptions(),
                CancellationToken.None));

        Assert.Equal(GitBackupErrorCodes.ConfigurationInvalid, exception.Code);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(RepositoryPath);
            Directory.CreateDirectory(RemotePath);
            Directory.CreateDirectory(HooksPath);
            File.WriteAllBytes(AttributesPath, []);
        }

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-repo-{Guid.NewGuid():N}");

        public string RepositoryPath => Path.Combine(Root, "repo");

        public string RemotePath => Path.Combine(Root, "remote.git");

        public string HooksPath => Path.Combine(Root, "hooks-empty");

        public string AttributesPath => Path.Combine(Root, "attributes-empty");

        public GitCommandRunner Git { get; } = new();

        public GitBackupOptions CreateOptions() =>
            GitBackupOptions.Disabled with
            {
                Enabled = true,
                RepositoryPath = RepositoryPath,
                Remote = "origin",
                Branch = "main",
                AllowFileRemote = true,
                SecureHooksPath = HooksPath,
                SecureAttributesPath = AttributesPath
            };

        public async Task InitializeBareRemoteAsync()
        {
            _ = await Git.RunAsync(
                RemotePath,
                ["init", "--bare"],
                environment: null,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
        }

        public async Task InitializeLocalRepositoryAsync()
        {
            _ = await Git.RunAsync(
                RepositoryPath,
                ["init", "-b", "main"],
                environment: null,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
            _ = await Git.RunAsync(
                RepositoryPath,
                ["remote", "add", "origin", RemotePath],
                environment: null,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.EnumerateFiles(
                    Root,
                    "*",
                    SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
