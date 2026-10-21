namespace CipherVault.Host.Contracts;

public sealed record HostApiResponse<T>(
    bool Ok,
    T Data);

public sealed record HostApiErrorResponse(
    bool Ok,
    HostApiError Error);

public sealed record HostApiError(
    string Code,
    string Message,
    string? RequestId);

public sealed record HostHealthData(
    string Status,
    string HostVersion,
    string ProtocolVersion);

public sealed record HostStatusData(
    string Status,
    string HostVersion,
    string ProtocolVersion,
    HostEndpointStatusData Endpoint,
    HostSessionStatusData Session);

public sealed record HostEndpointStatusData(
    string BaseUrl,
    string McpPath,
    string AdminPath);

public sealed record HostSessionStatusData(
    string State,
    Guid? SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastUserActivityAt,
    DateTimeOffset? IdleExpiresAt,
    DateTimeOffset? HardExpiresAt,
    string? LockReason,
    int ActiveLeases,
    int ConnectedClients);

public sealed record HostInitializeData(
    Guid VaultId,
    string VaultPath,
    string RecoveryKey,
    DateTimeOffset CreatedAt);

public sealed record HostClientData(
    string Id,
    string AccessMode,
    bool AllowLock,
    bool Enabled);
