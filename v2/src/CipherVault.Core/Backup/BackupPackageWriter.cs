using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Backup;

public sealed record BackupPackageWriteResult(
    BackupManifest Manifest,
    BackupEntryHashes Hashes);

public static class BackupPackageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static async Task<BackupPackageWriteResult> WriteAsync(
        string packagePath,
        Guid vaultId,
        DateTimeOffset createdAtUtc,
        string databasePath,
        string keyEnvelopePath,
        CancellationToken cancellationToken)
    {
        if (vaultId == Guid.Empty)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "VaultId must not be empty.");
        }

        PathSafety.EnsureNoLinkComponents(packagePath);
        PathSafety.EnsureNoLinkComponents(databasePath);
        PathSafety.EnsureNoLinkComponents(keyEnvelopePath);
        if (!PathSafety.IsRegularFile(databasePath)
            || !PathSafety.IsRegularFile(keyEnvelopePath))
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Backup source files must be regular files.");
        }

        var databaseInfo = new FileInfo(databasePath);
        var keyInfo = new FileInfo(keyEnvelopePath);
        if (databaseInfo.Length <= 0
            || databaseInfo.Length > BackupConstants.MaxUncompressedBytes)
        {
            throw new VaultException(
                VaultErrorCode.BackupTooLarge,
                "Backup database is outside the supported size range.");
        }
        if (keyInfo.Length is <= 0 or > BackupConstants.MaxKeyEnvelopeBytes)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Backup key envelope size is invalid.");
        }

        var databaseHash = await HashFileAsync(
            databasePath,
            cancellationToken);
        var keyHash = await HashFileAsync(
            keyEnvelopePath,
            cancellationToken);
        var manifest = new BackupManifest(
            BackupConstants.Format,
            BackupConstants.FormatVersion,
            vaultId,
            createdAtUtc.ToUniversalTime(),
            [
                new BackupManifestFile(
                    BackupConstants.DatabaseEntryName,
                    databaseHash.Bytes,
                    databaseHash.Sha256),
                new BackupManifestFile(
                    BackupConstants.KeyEnvelopeEntryName,
                    keyHash.Bytes,
                    keyHash.Sha256)
            ]);

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                format = manifest.Format,
                formatVersion = manifest.FormatVersion,
                vaultId = manifest.VaultId.ToString("D"),
                createdAtUtc = manifest.CreatedAtUtc.ToUniversalTime()
                    .ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                        System.Globalization.CultureInfo.InvariantCulture),
                files = manifest.Files.Select(file => new
                {
                    path = file.Path,
                    bytes = file.Bytes,
                    sha256 = file.Sha256
                })
            },
            JsonOptions);
        if (manifestBytes.Length > BackupConstants.MaxManifestBytes)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Backup manifest exceeds the supported size.");
        }

        try
        {
            await using var output = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            VaultFilePermissions.ProtectFile(packagePath);
            using (var archive = new ZipArchive(
                       output,
                       ZipArchiveMode.Create,
                       leaveOpen: true))
            {
                await WriteBytesEntryAsync(
                    archive,
                    BackupConstants.ManifestEntryName,
                    manifestBytes,
                    cancellationToken);
                await WriteFileEntryAsync(
                    archive,
                    BackupConstants.DatabaseEntryName,
                    databasePath,
                    cancellationToken);
                await WriteFileEntryAsync(
                    archive,
                    BackupConstants.KeyEnvelopeEntryName,
                    keyEnvelopePath,
                    cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            if (output.Length > BackupConstants.MaxPackageBytes)
            {
                throw new VaultException(
                    VaultErrorCode.BackupTooLarge,
                    "Backup package exceeds the supported size.");
            }
        }
        catch (VaultException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to write the encrypted backup package.",
                exception);
        }

        return new BackupPackageWriteResult(
            manifest,
            new BackupEntryHashes(
                databaseHash.Bytes,
                databaseHash.Sha256,
                keyHash.Bytes,
                keyHash.Sha256));
    }

    private static async Task WriteBytesEntryAsync(
        ZipArchive archive,
        string name,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, cancellationToken);
    }

    private static async Task WriteFileEntryAsync(
        ZipArchive archive,
        string name,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var destination = entry.Open();
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<(long Bytes, string Sha256)> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
            hash.AppendData(buffer, 0, read);
        }

        return (
            total,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
