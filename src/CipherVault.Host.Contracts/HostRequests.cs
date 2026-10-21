namespace CipherVault.Host.Contracts;

public sealed record HostStopRequest(
    string? Reason);

public sealed record HostUnlockRequest(
    string? CredentialType);

public sealed record HostLockRequest(
    string? Reason);

public sealed record HostInitializeRequest(
    string? VaultPath);

public sealed record HostCreateClientRequest(
    string? Id,
    string? AccessMode,
    bool? AllowLock);

public sealed record HostUpdateClientRequest(
    string? AccessMode,
    bool? AllowLock,
    bool? Enabled);

public sealed record HostCreateNoteRequest(
    string? Title,
    string? Folder,
    string? Format,
    string? Content,
    IReadOnlyList<string>? Tags);
