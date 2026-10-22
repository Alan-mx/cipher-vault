namespace CipherVault.Core.Documents;

public sealed record VaultCatalogItem(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    string Format,
    int Revision,
    IReadOnlyList<string> Headings,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt);

public sealed record VaultCatalogPage(
    IReadOnlyList<VaultCatalogItem> Items,
    string? NextCursor);

public sealed record VaultSearchHit(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    string Snippet,
    double Score);

public sealed record VaultSearchCoverage(
    int TotalDocuments,
    int ScannedDocuments,
    int MatchedDocuments,
    int ReturnedDocuments,
    bool IsTruncated);

public sealed record VaultSearchPage(
    IReadOnlyList<VaultSearchHit> Hits,
    VaultSearchCoverage Coverage,
    string? NextCursor);
