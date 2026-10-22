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
