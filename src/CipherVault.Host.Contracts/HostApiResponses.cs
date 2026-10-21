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
