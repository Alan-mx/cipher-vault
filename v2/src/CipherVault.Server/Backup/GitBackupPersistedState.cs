namespace CipherVault.Server.Backup;

public sealed record GitBackupPersistedState(
    int Version,
    string RepositoryFingerprint,
    bool Dirty,
    DateTimeOffset? FirstDirtyAtUtc,
    DateTimeOffset? LastMutationAtUtc,
    string? PendingLocalBackupFileName,
    string? PendingPushCommitSha,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastCommitSha,
    DateTimeOffset? LastPushAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    int ConsecutiveFailures,
    string? LastErrorCode)
{
    public const int CurrentVersion = 1;

    public static GitBackupPersistedState CreateInitial(
        string repositoryFingerprint,
        DateTimeOffset nowUtc) =>
        new(
            CurrentVersion,
            repositoryFingerprint,
            Dirty: true,
            FirstDirtyAtUtc: nowUtc.ToUniversalTime(),
            LastMutationAtUtc: nowUtc.ToUniversalTime(),
            PendingLocalBackupFileName: null,
            PendingPushCommitSha: null,
            LastSuccessAtUtc: null,
            LastCommitSha: null,
            LastPushAtUtc: null,
            LastAttemptAtUtc: null,
            NextAttemptAtUtc: null,
            ConsecutiveFailures: 0,
            LastErrorCode: null);
}

public sealed record GitBackupStateLoadResult(
    GitBackupPersistedState State,
    bool WasReset,
    string? SafeErrorCode);
