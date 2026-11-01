namespace CipherVault.Server.Backup;

public static class GitBackupErrorCodes
{
    public const string Disabled = "git_disabled";
    public const string ConfigurationInvalid = "git_config_invalid";
    public const string GitUnavailable = "git_unavailable";
    public const string RepositoryInvalid = "git_repository_invalid";
    public const string RepositoryBusy = "git_repository_busy";
    public const string StateCorrupt = "git_state_corrupt";
    public const string StateWriteFailed = "git_state_write_failed";
    public const string BackupBusy = "git_backup_busy";
    public const string SnapshotFailed = "git_snapshot_failed";
    public const string CommitFailed = "git_commit_failed";
    public const string PushFailed = "git_push_failed";
    public const string AuthenticationFailed = "git_auth_failed";
    public const string NonFastForward = "git_non_fast_forward";
    public const string Timeout = "git_timeout";
    public const string RotationRequired = "git_rotation_required";
    public const string InternalError = "git_internal_error";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            Disabled,
            ConfigurationInvalid,
            GitUnavailable,
            RepositoryInvalid,
            RepositoryBusy,
            StateCorrupt,
            StateWriteFailed,
            BackupBusy,
            SnapshotFailed,
            CommitFailed,
            PushFailed,
            AuthenticationFailed,
            NonFastForward,
            Timeout,
            RotationRequired,
            InternalError
        ],
        StringComparer.Ordinal);
}
