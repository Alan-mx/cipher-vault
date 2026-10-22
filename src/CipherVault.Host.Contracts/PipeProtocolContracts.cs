using System.Text.Json;

namespace CipherVault.Host.Contracts;

public sealed record PipeHelloRequest(
    string ProtocolVersion,
    string ClientNonceB64Url);

public sealed record PipeHelloResponse(
    string ProtocolVersion,
    string ServerNonceB64Url,
    string ServiceInstanceId,
    string ServiceSid,
    uint ServerProcessId,
    DateTimeOffset ServerProcessStartedAt,
    string ServerImageSha256,
    string ServerSignerThumbprint);

public sealed record PipeAuthenticateRequest(
    VerifiedProcessKind ClientKind,
    string TranscriptHashB64Url);

public sealed record PipeAuthenticateResponse(
    Guid ProcessSessionId,
    Guid? AdminSessionId,
    string OwnerSid,
    uint SessionId,
    DateTimeOffset ExpiresAt);

public sealed record PipePingResponse(
    DateTimeOffset ServerTime);

public sealed record PipeCloseRequest(
    string? Reason);

public sealed record PipeError(
    string Code,
    string Message);

public sealed record PipeRpcRequest(
    string ProtocolVersion,
    Guid RequestId,
    string Method,
    JsonElement Payload);

public sealed record PipeRpcResponse(
    Guid RequestId,
    bool Ok,
    JsonElement? Data,
    PipeError? Error);

public sealed record AdminPipeStatusData(
    string ServiceVersion,
    string ProtocolVersion,
    string SecurityMode,
    bool BrokerAvailable,
    HostSessionStatusData Session);

public sealed record AdminSecurityDiagnosticsData(
    string ServiceVersion,
    string ProtocolVersion,
    string SecurityMode,
    string OwnerSid,
    string? ServiceSid,
    uint OwnerSessionId,
    bool BrokerAvailable,
    string VaultState,
    Guid? VaultSessionId,
    AdminInstallConsistencyData Install);

public sealed record AdminInstallConsistencyData(
    bool ManifestLoaded,
    string? ErrorCode,
    Guid? InstallId,
    bool OwnerSidMatches,
    bool ServiceAccountMatches,
    bool PipesMatch);

public sealed record AdminStrictBridgeData(
    string Id,
    string AccessMode,
    bool Enabled,
    string? ImagePath,
    string? ImageSha256,
    string? SignerThumbprint,
    string? SignerSubject);
