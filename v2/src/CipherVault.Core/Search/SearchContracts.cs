using CipherVault.Core.Documents;

namespace CipherVault.Core.Search;

public sealed record SearchQuery(
    string Query,
    IReadOnlyList<string>? Tags,
    VaultDocumentFormat? Format,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    bool? HasBacklinks,
    bool OrphanOnly,
    CursorPageRequest Page);

public sealed record SearchCoverage(
    int TotalActiveDocuments,
    int ScannedDocuments,
    int MatchedDocuments,
    int ReturnedDocuments,
    bool Truncated);

public sealed record SearchHit(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    string Snippet,
    double Score);

public sealed record SearchPage(
    IReadOnlyList<SearchHit> Hits,
    SearchCoverage Coverage,
    string? NextCursor);
