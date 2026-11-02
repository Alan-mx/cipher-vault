using CipherVault.Application.Backup;
using CipherVault.Server.Backup;
using CipherVault.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CipherVault.Server.Tests;

public sealed class GitBackupCoordinatorTests
{
    [Fact]
    public async Task AutomaticallyCreatesBackupCommitsAndPushes()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var gitFixture = new GitFixture();
        await gitFixture.InitializeAsync();
        var commandRunner = new GitCommandRunner();
        var coordinator = CreateCoordinator(
            server,
            gitFixture.Options,
            commandRunner);

        await coordinator.RunOnceForTestAsync(CancellationToken.None);

        var status = await coordinator.GetStatusAsync();
        Assert.Equal(GitBackupState.Idle, status.State);
        Assert.False(status.Pending);
        Assert.False(status.PushPending);
        Assert.NotNull(status.LastSuccessAtUtc);
        Assert.NotNull(status.LastCommitShortSha);

        var remoteFile = await commandRunner.RunAsync(
            gitFixture.RemotePath,
            ["show", "main:latest.cvbackup"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.Equal(0, remoteFile.ExitCode);
        Assert.NotEmpty(remoteFile.StandardOutput);
    }

    [Fact]
    public async Task FailedPushIsRetriedBeforeCreatingAnotherSnapshot()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var gitFixture = new GitFixture();
        await gitFixture.InitializeAsync(createRemote: false);
        var runner = new GitCommandRunner();
        var coordinator = CreateCoordinator(
            server,
            gitFixture.Options,
            runner);

        await coordinator.RunOnceForTestAsync(CancellationToken.None);
        var failed = await coordinator.GetStatusAsync();
        Assert.Equal(GitBackupState.RetryWait, failed.State);
        Assert.True(failed.PushPending);
        Assert.Equal(GitBackupErrorCodes.PushFailed, failed.LastErrorCode);

        await gitFixture.CreateRemoteAsync();
        await coordinator.RunOnceForTestAsync(CancellationToken.None);

        var recovered = await coordinator.GetStatusAsync();
        Assert.Equal(GitBackupState.Idle, recovered.State);
        Assert.False(recovered.PushPending);
    }

    [Fact]
    public async Task RotationCreatesLocalBackupWithoutAddingAnotherCommit()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var gitFixture = new GitFixture();
        await gitFixture.InitializeAsync();
        var runner = new GitCommandRunner();
        var initialCoordinator = CreateCoordinator(
            server,
            gitFixture.Options,
            runner);
        await initialCoordinator.RunOnceForTestAsync(CancellationToken.None);
        var before = await runner.RunAsync(
            gitFixture.RepositoryPath,
            ["rev-list", "--count", "HEAD"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.Equal("1", before.StandardOutput.Trim());

        var rotatedOptions = gitFixture.Options with
        {
            RotationBytes = 1
        };
        var rotated = CreateCoordinator(
            server,
            rotatedOptions,
            runner);
        await rotated.NotifyAsync(
            CipherVault.Application.Backup.DocumentMutationKind.Update);
        await rotated.RunOnceForTestAsync(CancellationToken.None);

        var status = await rotated.GetStatusAsync();
        Assert.Equal(GitBackupState.RotationRequired, status.State);
        Assert.True(status.RotationRequired);
        var after = await runner.RunAsync(
            gitFixture.RepositoryPath,
            ["rev-list", "--count", "HEAD"],
            environment: null,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.Equal("1", after.StandardOutput.Trim());
    }

    [Fact]
    public void DueTimeUsesDebounceAndCapsContinuousChanges()
    {
        var options = GitBackupOptions.Disabled with
        {
            Enabled = true,
            RepositoryPath = Path.GetTempPath(),
            DebounceDelay = TimeSpan.FromMinutes(5),
            MaxDelay = TimeSpan.FromMinutes(30)
        };
        var state = GitBackupPersistedState.CreateInitial(
            new string('a', 64),
            DateTimeOffset.Parse("2026-11-01T12:00:00Z")) with
        {
            LastMutationAtUtc = DateTimeOffset.Parse("2026-11-01T12:03:00Z")
        };

        Assert.Equal(
            DateTimeOffset.Parse("2026-11-01T12:08:00Z"),
            GitBackupCoordinator.CalculateDueAt(state, options));

        state = state with
        {
            LastMutationAtUtc = DateTimeOffset.Parse("2026-11-01T12:29:00Z")
        };
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-01T12:30:00Z"),
            GitBackupCoordinator.CalculateDueAt(state, options));
    }

    [Fact]
    public async Task BlockedRepositoryDoesNotCreateOrCommitBackup()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var gitFixture = new GitFixture();
        await gitFixture.InitializeAsync();
        Directory.CreateDirectory(
            Path.Combine(gitFixture.RepositoryPath, ".git", "rebase-merge"));
        var coordinator = CreateCoordinator(
            server,
            gitFixture.Options,
            new GitCommandRunner());

        await coordinator.NotifyAsync(DocumentMutationKind.Update);
        await coordinator.RunOnceForTestAsync(CancellationToken.None);

        var status = await coordinator.GetStatusAsync();
        Assert.Equal(GitBackupState.RetryWait, status.State);
        Assert.Equal(GitBackupErrorCodes.RepositoryBusy, status.LastErrorCode);
        Assert.True(status.Pending);
        Assert.False(
            File.Exists(
                Path.Combine(
                    gitFixture.RepositoryPath,
                    "latest.cvbackup")));
    }

    private static GitBackupCoordinator CreateCoordinator(
        ServerTestApplication server,
        GitBackupOptions options,
        GitCommandRunner runner)
    {
        var backupStorage = new BackupStorageOptions(
            server.Options.Paths.BackupDirectory,
            server.Options.Paths.BackupLockPath);
        return new GitBackupCoordinator(
            options,
            new GitBackupStateStore(),
            new GitBackupRepository(runner),
            new BackupService(
                backupStorage,
                server.Manager,
                server.TimeProvider),
            backupStorage,
            server.Options.Paths,
            server.Manager,
            server.TimeProvider,
            NullLogger<GitBackupCoordinator>.Instance);
    }

    private sealed class GitFixture : IDisposable
    {
        public GitFixture()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(RepositoryPath);
            Directory.CreateDirectory(RemotePath);
        }

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-coordinator-{Guid.NewGuid():N}");

        public string RepositoryPath => Path.Combine(Root, "repo");

        public string RemotePath => Path.Combine(Root, "remote.git");

        public GitBackupOptions Options => GitBackupOptions.Disabled with
        {
            Enabled = true,
            RepositoryPath = RepositoryPath,
            Remote = "origin",
            Branch = "main"
        };

        public async Task InitializeAsync(bool createRemote = true)
        {
            var runner = new GitCommandRunner();
            if (createRemote)
            {
                await CreateRemoteAsync();
            }
            _ = await runner.RunAsync(
                RepositoryPath,
                ["init", "-b", "main"],
                environment: null,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
            _ = await runner.RunAsync(
                RepositoryPath,
                ["remote", "add", "origin", RemotePath],
                environment: null,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
        }

        public async Task CreateRemoteAsync()
        {
            var runner = new GitCommandRunner();
            _ = await runner.RunAsync(
                RemotePath,
                ["init", "--bare"],
                environment: null,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

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
