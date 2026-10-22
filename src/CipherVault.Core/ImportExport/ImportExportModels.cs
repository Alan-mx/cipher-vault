namespace CipherVault.Core.ImportExport;

public sealed record VaultImportPreviewItem(
    string RelativePath,
    string Format,
    long SizeBytes,
    string State,
    string? Reason);

public sealed record VaultImportPreview(
    Guid JobId,
    string SourceRoot,
    int TotalFiles,
    int SupportedFiles,
    int UnsupportedFiles,
    int ConflictFiles,
    IReadOnlyList<VaultImportPreviewItem> Items);

public sealed record VaultImportResult(
    Guid JobId,
    int Imported,
    int Skipped,
    int Failed,
    string State);

public sealed record VaultExportResult(
    string Destination,
    int ExportedDocuments,
    string ManifestPath,
    bool AuditRecorded = true);
