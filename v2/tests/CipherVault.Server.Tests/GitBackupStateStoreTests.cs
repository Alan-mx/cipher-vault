using System.Text;
using CipherVault.Server.Backup;

namespace CipherVault.Server.Tests;

public sealed class GitBackupStateStoreTests
{
    private static readonly string Fingerprint = new('a', 64);

    [Fact]
    public async Task MissingStateCreatesDirtyBaseline()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

        var result = await fixture.Store.LoadAsync(
            fixture.StatePath,
            Fingerprint,
            now);

        Assert.True(result.WasReset);
        Assert.Null(result.SafeErrorCode);
        Assert.True(result.State.Dirty);
        Assert.Equal(now, result.State.FirstDirtyAtUtc);
    }

    [Fact]
    public async Task StateRoundTripsAtomically()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var state = GitBackupPersistedState.CreateInitial(Fingerprint, now) with
        {
            PendingLocalBackupFileName =
                "cipher-vault-20261101-120000-a1b2c3d4.cvbackup",
            LastCommitSha = new string('b', 40),
            PendingPushCommitSha = new string('c', 40),
            ConsecutiveFailures = 2,
            LastErrorCode = GitBackupErrorCodes.PushFailed
        };

        await fixture.Store.SaveAsync(fixture.StatePath, state);
        var result = await fixture.Store.LoadAsync(
            fixture.StatePath,
            Fingerprint,
            now.AddMinutes(1));

        Assert.False(result.WasReset);
        Assert.Equal(state.PendingLocalBackupFileName, result.State.PendingLocalBackupFileName);
        Assert.Equal(state.PendingPushCommitSha, result.State.PendingPushCommitSha);
        Assert.Equal(2, result.State.ConsecutiveFailures);
        Assert.Equal(GitBackupErrorCodes.PushFailed, result.State.LastErrorCode);
    }

    [Fact]
    public async Task RepositoryFingerprintChangeStartsNewBaseline()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var state = GitBackupPersistedState.CreateInitial(Fingerprint, now) with
        {
            Dirty = false,
            FirstDirtyAtUtc = null,
            LastMutationAtUtc = null,
            LastCommitSha = new string('b', 40)
        };
        await fixture.Store.SaveAsync(fixture.StatePath, state);

        var result = await fixture.Store.LoadAsync(
            fixture.StatePath,
            new string('d', 64),
            now.AddMinutes(2));

        Assert.True(result.WasReset);
        Assert.True(result.State.Dirty);
        Assert.Null(result.State.LastCommitSha);
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"unknown\":true}")]
    [InlineData("{\"version\":1}")]
    public async Task InvalidStateResetsWithSafeError(string json)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.StatePath,
            json,
            new UTF8Encoding(false));

        var result = await fixture.Store.LoadAsync(
            fixture.StatePath,
            Fingerprint,
            DateTimeOffset.Parse("2026-11-01T12:00:00Z"));

        Assert.True(result.WasReset);
        Assert.Equal(GitBackupErrorCodes.StateCorrupt, result.SafeErrorCode);
        Assert.True(result.State.Dirty);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-state-{Guid.NewGuid():N}");

        public Fixture()
        {
            Directory.CreateDirectory(root);
        }

        public GitBackupStateStore Store { get; } = new();

        public string StatePath => Path.Combine(root, "git-backup-state.json");

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
