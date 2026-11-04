using CipherVault.Core.Documents;
using System.Text.Json;

namespace CipherVault.Core.Search;

public sealed record SearchQuery(
    string Query,
    IReadOnlyList<string>? Tags,
    VaultDocumentFormat? Format,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    bool? HasBacklinks,
    bool OrphanOnly,
    CursorPageRequest Page,
    IReadOnlyList<SearchPropertyFilter>? PropertyFilters = null,
    SearchPropertyMatch PropertyMatch = SearchPropertyMatch.All);

public enum SearchPropertyOperator
{
    Exists,
    Eq,
    Contains,
    In,
    Gte,
    Lte
}

public enum SearchPropertyMatch
{
    All,
    Any
}

public sealed record SearchPropertyFilter(
    string Field,
    SearchPropertyOperator Operator,
    JsonElement Value);

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
