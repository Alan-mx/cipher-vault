using CipherVault.Core.Backup;
using CipherVault.Server.Backup;

namespace CipherVault.Server.Api.Contracts;

public sealed record BackupListApiItem(
    string FileName,
    long Bytes,
    DateTimeOffset ModifiedAtUtc,
    DateTimeOffset? CreatedAtUtc,
    int? FormatVersion,
    string Status,
    string? ErrorCode)
{
    public static BackupListApiItem FromDomain(BackupListItem item) =>
        new(
            item.FileName,
            item.Bytes,
            item.ModifiedAtUtc,
            item.CreatedAtUtc,
            item.FormatVersion,
            ToApiStatus(item.Status),
            item.ErrorCode);

    private static string ToApiStatus(BackupFileStatus status) =>
        status switch
        {
            BackupFileStatus.ManifestReadable => "manifest_readable",
            BackupFileStatus.Invalid => "invalid",
            BackupFileStatus.Verified => "verified",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}

public sealed record BackupListApiResponse(
    IReadOnlyList<BackupListApiItem> Items,
    string? NextCursor);

public sealed record BackupCreateApiResponse(
    BackupListApiItem Backup);

public sealed record GitBackupStatusApiResponse(
    string State,
    bool Pending,
    bool PushPending,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastCommitShortSha,
    DateTimeOffset? LastPushAtUtc,
    string? LastErrorCode,
    bool RotationRequired,
    long? RepositoryObjectBytes)
{
    public static GitBackupStatusApiResponse FromDomain(
        GitBackupRuntimeStatus status) =>
        new(
            ToApiState(status.State),
            status.Pending,
            status.PushPending,
            status.NextAttemptAtUtc,
            status.LastSuccessAtUtc,
            status.LastCommitShortSha,
            status.LastPushAtUtc,
            status.LastErrorCode,
            status.RotationRequired,
            status.RepositoryObjectBytes);

    private static string ToApiState(GitBackupState state) =>
        state switch
        {
            GitBackupState.Disabled => "disabled",
            GitBackupState.Idle => "idle",
            GitBackupState.Pending => "pending",
            GitBackupState.Running => "running",
            GitBackupState.RetryWait => "retry_wait",
            GitBackupState.RotationRequired => "rotation_required",
            GitBackupState.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
}
