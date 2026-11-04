using CipherVault.Core.Documents;

namespace CipherVault.Core.Exports;

public static class PlaintextExportLimits
{
    public const int MaxDocuments = 10_000;
    public const long MaxContentBytes = 1024L * 1024 * 1024;
    public const int ManifestFormatVersion = 2;
    public const string ManifestFormat = "cipher-vault-plaintext-export";
    public const string ManifestFileName = "_cipher-vault-export.json";
    public const string ConfirmationPhrase = "导出明文";
}

public sealed record PlaintextExportPreflight(
    int DocumentCount,
    long TotalContentBytes);

public sealed record PlaintextExportDocument(
    Guid DocumentId,
    string VirtualPath,
    string Title,
    VaultDocumentFormat Format,
    int Revision,
    string Content,
    IReadOnlyList<ExportDocumentLink> Links,
    string PathKey = "");

public sealed record ExportDocumentLink(
    DocumentLinkKind Kind,
    string ResolutionStatus,
    string? TargetVirtualPath,
    string DisplayText,
    string? TargetHeading,
    int StartOffset,
    int EndOffset);

public sealed record PlaintextExportCursor(
    string PathKey,
    Guid DocumentId);
