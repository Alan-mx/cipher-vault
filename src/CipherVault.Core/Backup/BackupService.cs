using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Core.Session;
using CipherVault.Core.Storage;

namespace CipherVault.Core.Backup;

internal static class BackupService
{
    private const string ManifestName = "manifest.json";
    private const string DatabaseName = "vault.db";
    private const string KeysName = "vault.keys";
    private const string BackupFormat = "cipher-vault-backup";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<VaultBackupResult> CreateAsync(
        VaultSession session,
        string backupPath,
        CancellationToken cancellationToken)
    {
        var fullBackupPath = Path.GetFullPath(backupPath);
        if (File.Exists(fullBackupPath) || Directory.Exists(fullBackupPath))
        {
            throw new VaultException(VaultErrorCode.AlreadyExists, "Backup destination already exists.");
        }

        var parent = Path.GetDirectoryName(fullBackupPath)
            ?? throw new VaultException(VaultErrorCode.InvalidInput, "Backup path has no parent.");
        Directory.CreateDirectory(parent);
        var temporaryDirectory = Path.Combine(
            parent,
            $".{Path.GetFileName(fullBackupPath)}.tmp-{Guid.NewGuid():N}");
        var temporaryArchive = fullBackupPath + ".tmp";
        Directory.CreateDirectory(temporaryDirectory);
        VaultFileSecurity.ProtectDirectory(temporaryDirectory);
        try
        {
            var snapshotPath = Path.Combine(temporaryDirectory, DatabaseName);
            var keyPath = Path.Combine(session.VaultPath, KeysName);
            await SqlCipherVaultStore.CreateEncryptedSnapshotAsync(
                session.Connection,
                snapshotPath,
                session.DbKey,
                cancellationToken).ConfigureAwait(false);
            await SqlCipherVaultStore.VerifyAsync(
                temporaryDirectory,
                new UnlockedKeyMaterial(
                    session.VaultId,
                    session.MasterKey,
                    session.DbKey),
                cancellationToken).ConfigureAwait(false);
            File.Copy(keyPath, Path.Combine(temporaryDirectory, KeysName));
            VaultFileSecurity.ProtectFile(Path.Combine(temporaryDirectory, KeysName));

            var createdAt = DateTimeOffset.UtcNow;
            var manifest = new BackupManifest
            {
                Format = BackupFormat,
                FormatVersion = 1,
                VaultId = session.VaultId,
                CreatedAt = createdAt,
                DatabaseSha256 = await ComputeSha256Async(snapshotPath, cancellationToken)
                    .ConfigureAwait(false),
                KeysSha256 = await ComputeSha256Async(
                    Path.Combine(temporaryDirectory, KeysName),
                    cancellationToken).ConfigureAwait(false)
            };
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, ManifestName),
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            await using (var output = new FileStream(
                temporaryArchive,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddFile(archive, Path.Combine(temporaryDirectory, ManifestName), ManifestName);
                AddFile(archive, snapshotPath, DatabaseName);
                AddFile(archive, Path.Combine(temporaryDirectory, KeysName), KeysName);
            }
            VaultFileSecurity.ProtectFile(temporaryArchive);
            File.Move(temporaryArchive, fullBackupPath);
            VaultFileSecurity.ProtectFile(fullBackupPath);
            return new VaultBackupResult(
                fullBackupPath,
                session.VaultId,
                createdAt,
                new FileInfo(fullBackupPath).Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            if (File.Exists(temporaryArchive))
            {
                File.Delete(temporaryArchive);
            }
        }
    }

    public static async Task<VaultRestoreBackupResult> RestoreAsync(
        string backupPath,
        string targetPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken)
    {
        var fullBackupPath = Path.GetFullPath(backupPath);
        var fullTargetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(fullBackupPath))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Backup file was not found.");
        }
        if (Directory.Exists(fullTargetPath) || File.Exists(fullTargetPath))
        {
            throw new VaultException(VaultErrorCode.AlreadyExists, "Restore target must not exist.");
        }

        var parent = Path.GetDirectoryName(fullTargetPath)
            ?? throw new VaultException(VaultErrorCode.InvalidInput, "Restore target has no parent.");
        Directory.CreateDirectory(parent);
        var temporaryDirectory = Path.Combine(
            parent,
            $".{Path.GetFileName(fullTargetPath)}.restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        VaultFileSecurity.ProtectDirectory(temporaryDirectory);
        try
        {
            ExtractSafely(fullBackupPath, temporaryDirectory);
            var manifestPath = Path.Combine(temporaryDirectory, ManifestName);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                ?? throw new VaultException(VaultErrorCode.CorruptVault, "Backup manifest is empty.");
            if (manifest.Format != BackupFormat || manifest.FormatVersion != 1)
            {
                throw new VaultException(VaultErrorCode.CorruptVault, "Backup format is unsupported.");
            }

            var databasePath = Path.Combine(temporaryDirectory, DatabaseName);
            var keysPath = Path.Combine(temporaryDirectory, KeysName);
            await VerifySha256Async(databasePath, manifest.DatabaseSha256, cancellationToken)
                .ConfigureAwait(false);
            await VerifySha256Async(keysPath, manifest.KeysSha256, cancellationToken)
                .ConfigureAwait(false);

            var envelope = await VaultKeyring.LoadEnvelopeAsync(keysPath, cancellationToken)
                .ConfigureAwait(false);
            if (envelope.VaultId != manifest.VaultId)
            {
                throw new VaultException(VaultErrorCode.CorruptVault, "Backup identity mismatch.");
            }
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                credential,
                credentialType,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await SqlCipherVaultStore.VerifyAsync(
                    temporaryDirectory,
                    material,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }

            Directory.Move(temporaryDirectory, fullTargetPath);
            VaultFileSecurity.ProtectDirectory(fullTargetPath);
            return new VaultRestoreBackupResult(fullTargetPath, manifest.VaultId);
        }
        catch (InvalidDataException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Backup archive is invalid.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Backup metadata is invalid.",
                exception);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void ExtractSafely(string backupPath, string targetDirectory)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ManifestName,
            DatabaseName,
            KeysName
        };
        if (archive.Entries.Count != allowed.Count ||
            archive.Entries.Any(entry => !allowed.Contains(entry.FullName)))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Backup contains unexpected entries.");
        }
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(entry.FullName))
            {
                throw new VaultException(VaultErrorCode.CorruptVault, "Backup contains an unsafe entry.");
            }
            entry.ExtractToFile(Path.Combine(targetDirectory, entry.FullName));
        }
    }

    private static void AddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var source = File.OpenRead(sourcePath);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task VerifySha256Async(
        string path,
        string expectedHex,
        CancellationToken cancellationToken)
    {
        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Backup checksum mismatch.");
        }
    }

    private sealed record BackupManifest
    {
        public string Format { get; init; } = string.Empty;
        public int FormatVersion { get; init; }
        public Guid VaultId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string DatabaseSha256 { get; init; } = string.Empty;
        public string KeysSha256 { get; init; } = string.Empty;
    }
}
