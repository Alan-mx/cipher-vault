using System.Text.Json;

namespace CipherVault.Host.Contracts;

public sealed record BrokerCapabilitiesData(
    string Version,
    IReadOnlyList<string> Features);

public sealed record BrokerActivityReport(
    uint SessionId,
    long IdleMilliseconds,
    bool IsWorkstationLocked);

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

public sealed record BrokerTransferRequest(
    Guid TransferId,
    string Direction,
    string PayloadKind,
    string StagingPath,
    string SuggestedPath,
    DateTimeOffset ExpiresAt);

public sealed record BrokerTransferResult(
    Guid TransferId,
    bool Completed,
    string? FinalPath,
    string? ErrorCode);

public sealed record BridgeGrantRequest(
    IReadOnlyList<string> Scopes);

public sealed record BridgeGrantData(
    Guid GrantId,
    string ClientId,
    string AccessMode,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt);

public sealed record BridgeToolCallRequest(
    string ToolName,
    JsonElement Arguments);

public sealed record BridgeStatusData(
    bool GrantActive,
    string ClientId,
    string AccessMode,
    Guid ProcessSessionId,
    Guid BridgeInstanceId,
    DateTimeOffset SessionExpiresAt,
    IReadOnlyList<string> GrantScopes,
    DateTimeOffset? GrantExpiresAt);

public sealed record BridgeSessionCloseRequest(
    string? Reason);

public sealed record BridgeSessionCloseData(
    string Reason);
