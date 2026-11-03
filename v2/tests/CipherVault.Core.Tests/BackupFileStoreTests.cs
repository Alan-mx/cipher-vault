using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Tests;

public sealed class BackupFileStoreTests
{
    [Fact]
    public async Task ReplacingPathAfterPreflightDoesNotReturnVerified()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string fileName =
                "cipher-vault-20261101-010000-aaaaaaaa.cvbackup";
            var path = Path.Combine(root, fileName);
            var originalCreatedAt = new DateTimeOffset(
                2026,
                11,
                1,
                1,
                0,
                0,
                TimeSpan.Zero);
            var replacementCreatedAt = originalCreatedAt.AddHours(1);
            await CreatePackageAsync(root, fileName, originalCreatedAt);
            var replacementPath = Path.Combine(root, "replacement.cvbackup");
            await CreatePackageAsync(
                root,
                "replacement.cvbackup",
                replacementCreatedAt);
            var displacedPath = Path.Combine(root, "displaced.cvbackup");

            var cache = new VerifiedBackupCache();
            var identity = RegularFileIdentity.Read(path);
            if (!identity.IsReliable)
            {
                return;
            }

            cache.Set(
                identity.CanonicalPath,
                new VerifiedBackupCacheEntry(
                    identity,
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    new string('a', 64),
                    originalCreatedAt));

            var entry = await BackupFileStore.InspectFileAsync(
                path,
                fileName,
                cache,
                CancellationToken.None,
                _ =>
                {
                    File.Move(path, displacedPath);
                    File.Move(replacementPath, path);
                    return Task.CompletedTask;
                });

            Assert.NotNull(entry);
            Assert.Equal(
                BackupFileStatus.ManifestReadable,
                entry.Item.Status);
            Assert.Equal(originalCreatedAt, entry.Item.CreatedAtUtc);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task ListReturnsManifestReadableAndInvalidEntriesWithStableCursor()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await CreatePackageAsync(
                root,
                "cipher-vault-20261101-010000-aaaaaaaa.cvbackup",
                new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
            await CreatePackageAsync(
                root,
                "cipher-vault-20261101-020000-bbbbbbbb.cvbackup",
                new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero));
            await CreatePackageAsync(
                root,
                "cipher-vault-20261101-030000-cccccccc.cvbackup",
                new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero));
            var invalidPath = Path.Combine(
                root,
                "cipher-vault-20261101-040000-dddddddd.cvbackup");
            await File.WriteAllTextAsync(
                invalidPath,
                "not a zip");
            File.SetLastWriteTimeUtc(
                invalidPath,
                new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc));

            var cache = new VerifiedBackupCache();
            var now = new DateTimeOffset(
                2026,
                11,
                2,
                0,
                0,
                0,
                TimeSpan.Zero);
            var first = await BackupFileStore.ListAsync(
                root,
                new BackupQuery(new CursorPageRequest(2)),
                cache,
                now,
                CancellationToken.None);
            var second = await BackupFileStore.ListAsync(
                root,
                new BackupQuery(
                    new CursorPageRequest(2, first.NextCursor)),
                cache,
                now,
                CancellationToken.None);

            Assert.Equal(2, first.Items.Count);
            Assert.Equal(2, second.Items.Count);
            Assert.Equal(
                [
                    "cipher-vault-20261101-040000-dddddddd.cvbackup",
                    "cipher-vault-20261101-030000-cccccccc.cvbackup"
                ],
                first.Items.Select(item => item.FileName));
            Assert.Equal(BackupFileStatus.Invalid, first.Items[0].Status);
            Assert.Equal(
                BackupFileStatus.ManifestReadable,
                first.Items[1].Status);
            Assert.Null(second.NextCursor);
            Assert.All(
                first.Items.Concat(second.Items),
                item => Assert.DoesNotContain(root, item.FileName));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task CreatePackageAsync(
        string root,
        string fileName,
        DateTimeOffset createdAtUtc)
    {
        var path = Path.Combine(root, fileName);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true);
        var database = Encoding.UTF8.GetBytes("database");
        var key = Encoding.UTF8.GetBytes("key");
        var manifest = JsonSerializer.Serialize(new
        {
            format = BackupConstants.Format,
            formatVersion = BackupConstants.FormatVersion,
            vaultId = "11111111-1111-1111-1111-111111111111",
            createdAtUtc = createdAtUtc.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
            files = new[]
            {
                new
                {
                    path = BackupConstants.DatabaseEntryName,
                    bytes = database.Length,
                    sha256 = Convert.ToHexString(
                        SHA256.HashData(database)).ToLowerInvariant()
                },
                new
                {
                    path = BackupConstants.KeyEnvelopeEntryName,
                    bytes = key.Length,
                    sha256 = Convert.ToHexString(
                        SHA256.HashData(key)).ToLowerInvariant()
                }
            }
        });
        await WriteEntryAsync(
            archive,
            BackupConstants.ManifestEntryName,
            Encoding.UTF8.GetBytes(manifest));
        await WriteEntryAsync(
            archive,
            BackupConstants.DatabaseEntryName,
            database);
        await WriteEntryAsync(
            archive,
            BackupConstants.KeyEnvelopeEntryName,
            key);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content);
    }

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
