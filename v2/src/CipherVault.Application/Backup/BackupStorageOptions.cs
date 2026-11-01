using CipherVault.Core.Backup;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Application.Backup;

public sealed class BackupStorageOptions
{
    public BackupStorageOptions(
        string backupDirectory,
        string? backupLockPath = null)
    {
        BackupDirectory = PathSafety.Canonicalize(
            backupDirectory,
            "CIPHERVAULT_BACKUP_DIR");
        BackupLockPath = backupLockPath is null
            ? Path.Combine(
                BackupDirectory,
                BackupConstants.BackupLockFileName)
            : PathSafety.Canonicalize(
                backupLockPath,
                "Backup lock path");
        if (!PathSafety.GetRelation(
                BackupLockPath,
                BackupDirectory).IsChildOf)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Backup lock file must be inside the backup directory.");
        }
    }

    public string BackupDirectory { get; }

    public string BackupLockPath { get; }
}
