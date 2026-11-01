namespace CipherVault.Server.Backup;

public enum GitBackupState
{
    Disabled,
    Idle,
    Pending,
    Running,
    RetryWait,
    RotationRequired,
    Failed
}

public sealed record GitBackupRuntimeStatus(
    GitBackupState State,
    bool Pending,
    bool PushPending,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastCommitShortSha,
    DateTimeOffset? LastPushAtUtc,
    string? LastErrorCode,
    bool RotationRequired,
    long? RepositoryObjectBytes);

public enum GitBackupWakeupReason
{
    DocumentMutation,
    VaultUnlocked,
    TimerElapsed,
    RetryElapsed,
    ConfigurationChanged,
    Shutdown
}

public sealed record GitBackupWakeup(
    GitBackupWakeupReason Reason,
    DateTimeOffset ObservedAtUtc);

public sealed record GitRepositoryInspection(
    string Branch,
    string? HeadCommitSha,
    bool HasUnbornHead,
    long ObjectBytes,
    DateTimeOffset? FirstCommitAtUtc,
    bool HasMergeOrRebaseState,
    bool HasConflicts);
