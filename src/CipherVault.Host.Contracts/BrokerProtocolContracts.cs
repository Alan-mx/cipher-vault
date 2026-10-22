namespace CipherVault.Host.Contracts;

public sealed record BrokerCapabilitiesData(
    string Version,
    IReadOnlyList<string> Features);

public sealed record BrokerCredentialPromptRequest(
    Guid RequestId,
    string NonceB64Url,
    string Preference,
    string Message,
    DateTimeOffset ExpiresAt);

public sealed record BrokerCredentialPromptResult(
    Guid RequestId,
    string NonceB64Url,
    bool Confirmed,
    string? Credential,
    string CredentialType,
    string? DenialReason);

public sealed record BrokerApprovalRequest(
    Guid ApprovalId,
    string NonceB64Url,
    string OperationKind,
    string DisplayTitle,
    string DisplayDetails,
    string ParametersHash,
    DateTimeOffset ExpiresAt);

public sealed record BrokerApprovalDecision(
    Guid ApprovalId,
    string NonceB64Url,
    bool Approved,
    string? DenialReason);
