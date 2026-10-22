namespace CipherVault.Host.Security;

public sealed record TransferFileEntry(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record TransferManifest(
    string Format,
    int FormatVersion,
    Guid TransferId,
    HostTransferDirection Direction,
    HostTransferPayloadKind PayloadKind,
    IReadOnlyList<TransferFileEntry> Files,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
