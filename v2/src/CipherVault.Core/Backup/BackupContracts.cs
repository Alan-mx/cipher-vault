using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Documents;
using CipherVault.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace CipherVault.Core.Backup;

public enum BackupFileStatus
{
    ManifestReadable,
    Invalid,
    Verified
}

public enum PlatformIdentityKind
{
    Unreliable,
    WindowsFileId128,
    LinuxStatx
}

public sealed record BackupListItem(
    string FileName,
    long Bytes,
    DateTimeOffset ModifiedAtUtc,
    DateTimeOffset? CreatedAtUtc,
    int? FormatVersion,
    BackupFileStatus Status,
    string? ErrorCode);

public sealed record BackupPage(
    IReadOnlyList<BackupListItem> Items,
    string? NextCursor);

public sealed record BackupQuery(
    CursorPageRequest Page);

public sealed record RegularFileIdentity(
    string CanonicalPath,
    long Bytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    long IdentityStamp,
    PlatformIdentityKind PlatformIdentityKind =
        PlatformIdentityKind.Unreliable,
    ulong VolumeId = 0,
    ulong FileIdLow = 0,
    ulong FileIdHigh = 0,
    long ChangeTimeSeconds = 0,
    long ChangeTimeNanoseconds = 0)
{
    public static RegularFileIdentity Read(string path)
    {
        PathSafety.EnsureNoLinkComponents(path);
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Read(handle, path);
    }

    internal static RegularFileIdentity Read(
        SafeFileHandle handle,
        string path)
    {
        var attributes = File.GetAttributes(handle);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("File is not a regular file.");
        }

        var canonical = PathSafety.Canonicalize(
            path,
            "Backup file");
        var created = File.GetCreationTimeUtc(handle).ToUniversalTime();
        var modified = File.GetLastWriteTimeUtc(handle).ToUniversalTime();
        var bytes = RandomAccess.GetLength(handle);
        var platform = PlatformFileIdentityReader.Read(handle);
        return new RegularFileIdentity(
            canonical,
            bytes,
            created,
            modified,
            ComputeIdentityStamp(
                canonical,
                bytes,
                created,
                modified),
            platform.Kind,
            platform.VolumeId,
            platform.FileIdLow,
            platform.FileIdHigh,
            platform.ChangeTimeSeconds,
            platform.ChangeTimeNanoseconds);
    }

    public bool IsReliable =>
        PlatformIdentityKind
            is PlatformIdentityKind.WindowsFileId128
                or PlatformIdentityKind.LinuxStatx
        && (FileIdLow != 0 || FileIdHigh != 0)
        && (ChangeTimeSeconds != 0 || ChangeTimeNanoseconds != 0);

    private static long ComputeIdentityStamp(
        string canonicalPath,
        long bytes,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{canonicalPath}\u001F{bytes}\u001F{createdAt.UtcTicks}\u001F{modifiedAt.UtcTicks}");
        var hash = SHA256.HashData(material);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }
}

public sealed class BackupDownloadLease : IAsyncDisposable
{
    private int disposed;
    private int terminalAttempted;

    public BackupDownloadLease(
        string fileName,
        long bytes,
        RegularFileIdentity identity,
        Stream content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(content);
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        FileName = fileName;
        Bytes = bytes;
        Identity = identity;
        Content = content;
    }

    public string FileName { get; }

    public Guid DownloadId { get; } = Guid.NewGuid();

    public long Bytes { get; }

    public RegularFileIdentity Identity { get; }

    public Stream Content { get; }

    public bool TryBeginTerminalAttempt() =>
        Interlocked.Exchange(ref terminalAttempted, 1) == 0;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await Content.DisposeAsync();
    }
}

public sealed record BackupCursorPayload(
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset EffectiveCreatedAtUtc,
    string FileName);
