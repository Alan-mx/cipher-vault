namespace CipherVault.Host.Contracts;

public enum VerifiedProcessKind
{
    HostService,
    AdminUi,
    AdminCli,
    AuthBroker,
    McpBridge
}

public enum ProcessVerificationStatus
{
    Verified,
    ProcessNotFound,
    AccessDenied,
    IdentityMismatch,
    HashInvalid,
    SignatureInvalid,
    Error
}

public sealed record ProcessImageIdentity(
    VerifiedProcessKind Kind,
    string FinalImagePath,
    string ImageSha256,
    string? SignerThumbprint,
    string? SignerSubject);

public sealed record VerifiedProcessIdentity(
    VerifiedProcessKind Kind,
    uint ProcessId,
    DateTimeOffset ProcessStartedAt,
    string UserSid,
    string LogonSid,
    uint SessionId,
    string FinalImagePath,
    string ImageSha256,
    string? SignerThumbprint,
    string? SignerSubject,
    int IntegrityLevel);

public sealed record ProcessVerificationPolicy(
    VerifiedProcessKind ExpectedKind,
    string? ExpectedUserSid,
    string? ExpectedLogonSid,
    uint? ExpectedSessionId,
    IReadOnlyCollection<ProcessImageIdentity> AllowedImages,
    IReadOnlyCollection<string> AllowedSignerThumbprints,
    bool RequireSha256,
    bool RequireSignature);
