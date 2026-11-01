namespace CipherVault.Application.Exports;

public sealed record PlaintextExportMetadata(
    string FileName,
    int DocumentCount,
    long TotalContentBytes);

public sealed record PlaintextExportManifest(
    string Format,
    int FormatVersion,
    DateTimeOffset ExportedAtUtc,
    int DocumentCount,
    long TotalContentBytes,
    IReadOnlyList<PlaintextExportManifestDocument> Documents);

public sealed record PlaintextExportManifestDocument(
    string VirtualPath,
    string Format,
    int Revision,
    long Bytes,
    string Sha256);

public sealed record PlaintextExportResult(
    int DocumentCount,
    long TotalContentBytes,
    long ZipBytes);
