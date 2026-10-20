namespace CipherVault.Core.Errors;

public enum VaultErrorCode
{
    InvalidInput,
    AlreadyExists,
    NotFound,
    InvalidCredential,
    Conflict,
    CorruptVault,
    SessionLocked,
    SessionExpired,
    RecoveryVerificationRequired,
    UnsupportedPlatform,
    StorageError
}

public sealed class VaultException : Exception
{
    public VaultException(VaultErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public VaultException(VaultErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public VaultErrorCode Code { get; }
}
