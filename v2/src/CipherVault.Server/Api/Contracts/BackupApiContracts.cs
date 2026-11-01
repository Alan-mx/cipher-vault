using CipherVault.Core.Backup;

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
