using System.IO.Compression;
using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Backup;

public static class BackupPackageVerifier
{
    public static async Task<BackupVerificationResult> VerifyAsync(
        BackupVerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Credential);
        var packagePath = PathSafety.Canonicalize(
            request.PackagePath,
            "Backup package path");
        var workingDirectory = PathSafety.Canonicalize(
            request.WorkingDirectory,
            "Backup verification directory");
        if (!PathSafety.IsRegularFile(packagePath))
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup package is not a regular file.");
        }
        if (Directory.Exists(workingDirectory)
            && Directory.EnumerateFileSystemEntries(workingDirectory).Any())
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup verification directory must be empty.");
        }

        Directory.CreateDirectory(workingDirectory);
        VaultFilePermissions.ProtectDirectory(workingDirectory);
        PathSafety.EnsureNoLinkComponents(workingDirectory);
        var databasePath = Path.Combine(
            workingDirectory,
            BackupConstants.DatabaseEntryName);
        var keyPath = Path.Combine(
            workingDirectory,
            BackupConstants.KeyEnvelopeEntryName);
        try
        {
            var initialIdentity = RegularFileIdentity.Read(packagePath);
            var preflight = await BackupZipPreflight.InspectAsync(
                packagePath,
                cancellationToken);
            var manifest = await BackupManifestReader.ReadAsync(
                packagePath,
                preflight,
                cancellationToken);
            var hashes = await ExtractAndHashAsync(
                packagePath,
                preflight,
                databasePath,
                keyPath,
                cancellationToken);
            ValidateHashes(manifest, hashes);
            var wholeFileSha256 = await HashFileAsync(
                packagePath,
                cancellationToken);
            if (RegularFileIdentity.Read(packagePath) != initialIdentity)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup package changed during validation.");
            }
            var envelope = await VaultKeyEnvelopeSerializer.LoadAsync(
                keyPath,
                cancellationToken);
            if (envelope.VaultId != manifest.VaultId)
            {
                throw Invalid(
                    "restore_manifest_invalid",
                    "Manifest and Key Envelope VaultId do not match.");
            }

            VaultKeyMaterial? passwordMaterial = null;
            ReadOnlyMemory<byte> databaseKey;
            try
            {
                if (request.Credential is
                    SnapshotDatabaseKeyCredential snapshotCredential)
                {
                    databaseKey = snapshotCredential.DatabaseKey;
                }
                else if (request.Credential is
                         InteractivePasswordCredential passwordCredential)
                {
                    passwordMaterial = await VaultKeyService.UnlockAsync(
                        keyPath,
                        passwordCredential.Password,
                        cancellationToken);
                    databaseKey = passwordMaterial.DatabaseKey;
                }
                else
                {
                    throw new VaultException(
                        VaultErrorCode.InvalidInput,
                        "Backup credential type is not supported.");
                }

                var database = await ValidateDatabaseAsync(
                    workingDirectory,
                    databaseKey,
                    manifest.VaultId,
                    request.SchemaPolicy,
                    cancellationToken);
                return new BackupVerificationResult(
                    manifest,
                    hashes.DatabaseSha256,
                    hashes.KeyEnvelopeSha256,
                    wholeFileSha256,
                    envelope.VaultId,
                    database.VaultId,
                    database.SchemaVersion);
            }
            finally
            {
                passwordMaterial?.Dispose();
            }
        }
        finally
        {
            BackupDatabaseStore.DeleteDatabaseSidecars(workingDirectory);
        }
    }

    private static async Task<BackupEntryHashes> ExtractAndHashAsync(
        string packagePath,
        BackupZipPreflightResult preflight,
        string databasePath,
        string keyPath,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var archive = new ZipArchive(
            file,
            ZipArchiveMode.Read,
            leaveOpen: true);
        var database = await ExtractEntryAsync(
            archive,
            preflight.GetRequired(BackupConstants.DatabaseEntryName),
            databasePath,
            BackupConstants.MaxUncompressedBytes,
            cancellationToken);
        var key = await ExtractEntryAsync(
            archive,
            preflight.GetRequired(BackupConstants.KeyEnvelopeEntryName),
            keyPath,
            BackupConstants.MaxKeyEnvelopeBytes,
            cancellationToken);
        return new BackupEntryHashes(
            database.Bytes,
            database.Sha256,
            key.Bytes,
            key.Sha256);
    }

    private static async Task<(long Bytes, string Sha256)> ExtractEntryAsync(
        ZipArchive archive,
        BackupZipEntryDescriptor descriptor,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(descriptor.Name)
            ?? throw Invalid(
                "restore_archive_invalid",
                "Backup entry is missing.");
        await using var source = entry.Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        VaultFilePermissions.ProtectFile(destinationPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > maximumBytes)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup entry exceeds the supported size.");
            }
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        if (total != descriptor.UncompressedBytes)
        {
            throw Invalid(
                "restore_hash_mismatch",
                "Backup entry size differs from the ZIP directory.");
        }
        return (
            total,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void ValidateHashes(
        BackupManifest manifest,
        BackupEntryHashes actual)
    {
        var database = manifest.Files.Single(file =>
            file.Path == BackupConstants.DatabaseEntryName);
        var key = manifest.Files.Single(file =>
            file.Path == BackupConstants.KeyEnvelopeEntryName);
        if (database.Bytes != actual.DatabaseBytes
            || !string.Equals(
                database.Sha256,
                actual.DatabaseSha256,
                StringComparison.Ordinal)
            || key.Bytes != actual.KeyEnvelopeBytes
            || !string.Equals(
                key.Sha256,
                actual.KeyEnvelopeSha256,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "restore_hash_mismatch",
                "Backup entry hash or size does not match the manifest.");
        }
    }

    private static async Task<VaultDatabaseInfo> ValidateDatabaseAsync(
        string directory,
        ReadOnlyMemory<byte> databaseKey,
        Guid expectedVaultId,
        BackupSchemaPolicy schemaPolicy,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection =
                await SqliteMcDatabaseFactory.OpenReadOnlyConnectionAsync(
                    directory,
                    databaseKey,
                    cancellationToken);
            var integrity =
                await SqliteMcDatabaseFactory.ExecuteScalarStringAsync(
                    connection,
                    "PRAGMA integrity_check;",
                    cancellationToken);
            if (!string.Equals(
                    integrity,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    "restore_database_invalid",
                    "Backup database integrity check failed.");
            }

            var info = await DatabaseInitializer.ReadAndValidateInfoAsync(
                connection,
                cancellationToken);
            if (info.VaultId != expectedVaultId)
            {
                throw Invalid(
                    "restore_database_invalid",
                    "Backup database VaultId does not match the manifest.");
            }
            var schemaSupported = schemaPolicy switch
            {
                BackupSchemaPolicy.CurrentOnly =>
                    info.SchemaVersion == DatabaseMigrationCatalog.CurrentVersion,
                BackupSchemaPolicy.AllowSupportedOlderVersions =>
                    BackupCompatibilityPolicy.IsSupported(info.SchemaVersion),
                _ => throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Backup Schema policy is not supported.")
            };
            if (!schemaSupported)
            {
                throw Invalid(
                    "restore_schema_unsupported",
                    "Backup database Schema is not supported.");
            }
            return info;
        }
        catch (BackupPackageValidationException)
        {
            throw;
        }
        catch (VaultException exception)
            when (exception.Code == VaultErrorCode.InvalidCredential)
        {
            throw Invalid(
                "restore_unlock_failed",
                "Backup password could not unlock the database.",
                exception);
        }
        catch (VaultException exception)
            when (exception.Code == VaultErrorCode.UnsupportedVersion)
        {
            throw Invalid(
                "restore_schema_unsupported",
                "Backup database Schema is not supported.",
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or VaultException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw Invalid(
                "restore_database_invalid",
                "Backup database could not be validated.",
                exception);
        }
    }

    private static async Task<string> HashFileAsync(
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
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static BackupPackageValidationException Invalid(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);
}
