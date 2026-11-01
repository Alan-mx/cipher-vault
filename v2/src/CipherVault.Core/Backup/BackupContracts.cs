using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Documents;
using CipherVault.Core.Security;

namespace CipherVault.Core.Backup;

public enum BackupFileStatus
{
    ManifestReadable,
    Invalid,
    Verified
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
    long IdentityStamp)
{
    public static RegularFileIdentity Read(string path)
    {
        PathSafety.EnsureNoLinkComponents(path);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("File is not a regular file.");
        }

        var canonical = PathSafety.Canonicalize(
            info.FullName,
            "Backup file");
        var created = info.CreationTimeUtc.ToUniversalTime();
        var modified = info.LastWriteTimeUtc.ToUniversalTime();
        return new RegularFileIdentity(
            canonical,
            info.Length,
            created,
            modified,
            ComputeIdentityStamp(
                canonical,
                info.Length,
                created,
                modified));
    }

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

    public long Bytes { get; }

    internal RegularFileIdentity Identity { get; }

    public Stream Content { get; }

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
