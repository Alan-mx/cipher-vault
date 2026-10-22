using CipherVault.Host.Contracts;

namespace CipherVault.Security.Win;

public sealed record ProcessVerificationResult(
    ProcessVerificationStatus Status,
    VerifiedProcessEvidence? Evidence,
    string? ReasonCode)
{
    public static ProcessVerificationResult Verified(
        VerifiedProcessEvidence evidence)
    {
        return new ProcessVerificationResult(
            ProcessVerificationStatus.Verified,
            evidence,
            ReasonCode: null);
    }

    public static ProcessVerificationResult Failed(
        ProcessVerificationStatus status,
        string reasonCode)
    {
        return new ProcessVerificationResult(
            status,
            Evidence: null,
            reasonCode);
    }
}
