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
            environment: null,
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

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(RepositoryPath);
            Directory.CreateDirectory(RemotePath);
        }

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-repo-{Guid.NewGuid():N}");

        public string RepositoryPath => Path.Combine(Root, "repo");

        public string RemotePath => Path.Combine(Root, "remote.git");

        public GitCommandRunner Git { get; } = new();

        public GitBackupOptions CreateOptions() =>
            GitBackupOptions.Disabled with
            {
                Enabled = true,
                RepositoryPath = RepositoryPath,
                Remote = "origin",
                Branch = "main"
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
