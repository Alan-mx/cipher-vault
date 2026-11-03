using CipherVault.Core.Database;

namespace CipherVault.Core.Backup;

public enum BackupSchemaPolicy
{
    CurrentOnly,
    AllowSupportedOlderVersions
}

internal static class BackupCompatibilityPolicy
{
    public const int MinimumSupportedSchemaVersion = 5;

    public static bool IsSupported(int schemaVersion) =>
        schemaVersion >= MinimumSupportedSchemaVersion
        && schemaVersion <= DatabaseMigrationCatalog.CurrentVersion;
}
