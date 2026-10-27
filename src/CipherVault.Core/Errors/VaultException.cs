namespace CipherVault.Core.Errors;

public enum VaultErrorCode
{
    InvalidInput,
    AlreadyExists,
    NotFound,
    InvalidCredential,
    AccessDenied,
    Conflict,
    CorruptVault,
    SessionLocked,
    SessionExpired,
    RecoveryVerificationRequired,
    UnsupportedPlatform,
    PatchTargetNotFound,
    FrontMatterInvalid,
    LinkIndexTooLarge,
    InvalidCursor,
    ClockRollback,
    StorageError
}

public sealed class VaultException : Exception
{
    public VaultException(VaultErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public VaultException(
        VaultErrorCode code,
        string message,
        IReadOnlyDictionary<string, object?> details)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public VaultException(VaultErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public VaultErrorCode Code { get; }

    public IReadOnlyDictionary<string, object?>? Details { get; }
}
