namespace CipherVault.Core.Documents;

public sealed record VaultReadResult(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    string Content,
    int Offset,
    int NextOffset,
    int TotalLength,
    bool Done,
    IReadOnlyList<string> Headings);

public sealed record VaultScanPage(
    IReadOnlyList<VaultCatalogItem> Items,
    string? NextCursor,
    bool Done);
