namespace CipherVault.Core.Backup;

public static class BackupConstants
{
    public const string Format = "cipher-vault-backup";
    public const int FormatVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string DatabaseEntryName = "vault.db";
    public const string KeyEnvelopeEntryName = "vault.key";
    public const string BackupExtension = ".cvbackup";
    public const string BackupFilePrefix = "cipher-vault-";
    public const string BackupTempPrefix = ".cipher-vault-backup-";
    public const string BackupLockFileName = ".cipher-vault-backup.lock";
    public const string RestoreTempPrefix = ".cipher-vault-restore-";
    public const string PartialSuffix = ".partial";
    public const long MaxPackageBytes = 8L * 1024 * 1024 * 1024;
    public const long MaxUncompressedBytes = 8L * 1024 * 1024 * 1024;
    public const long MaxManifestBytes = 64 * 1024;
    public const int MaxKeyEnvelopeBytes = 16 * 1024;
    public const int MaxCompressionRatio = 20;
    public const int RequiredEntryCount = 3;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int VerifiedCacheCapacity = 64;
}
