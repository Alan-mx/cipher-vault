namespace CipherVault.Core.Errors;

public enum VaultErrorCode
{
    InvalidInput,
    InvalidCredential,
    CorruptVault,
    UnsupportedVersion,
    UnsupportedPlatform,
    StorageError,
    DatabaseError,
    VaultNotInitialized,
    VaultAlreadyInitialized,
    VaultLocked,
    VaultBusy,
    VaultLockFailed,
    DocumentNotFound,
    DocumentPathConflict,
    RevisionConflict,
    FrontMatterInvalid,
    PatchTargetNotFound,
    PatchTargetAmbiguous,
    DocumentTooLarge,
    TagLimitExceeded,
    ImportPreviewInvalid,
    ImportSourceInvalid,
    InvalidCursor,
    Forbidden,
    PurgeNotEligible,
    BackupInProgress,
    BackupTooLarge,
    BackupNotFound,
    ExportTooLarge
}
