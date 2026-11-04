using CipherVault.Core.Documents;
using CipherVault.Core.Semantic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CipherVault.Core.Search;

public sealed record SearchQuery(
    string Query,
    SearchMode Mode,
    IReadOnlyList<string>? Tags,
    VaultDocumentFormat? Format,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    bool? HasBacklinks,
    bool OrphanOnly,
    CursorPageRequest Page,
    IReadOnlyList<SearchPropertyFilter>? PropertyFilters = null,
    SearchPropertyMatch PropertyMatch = SearchPropertyMatch.All)
{
    public SearchQuery(
        string Query,
        IReadOnlyList<string>? Tags,
        VaultDocumentFormat? Format,
        DateTimeOffset? UpdatedAfter,
        DateTimeOffset? UpdatedBefore,
        bool? HasBacklinks,
        bool OrphanOnly,
        CursorPageRequest Page,
        IReadOnlyList<SearchPropertyFilter>? PropertyFilters = null,
        SearchPropertyMatch PropertyMatch = SearchPropertyMatch.All)
        : this(
            Query,
            SearchMode.Keyword,
            Tags,
            Format,
            UpdatedAfter,
            UpdatedBefore,
            HasBacklinks,
            OrphanOnly,
            Page,
            PropertyFilters,
            PropertyMatch)
    {
    }
}

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
    double Score)
{
    public SearchRetrievalSource[] RetrievalSources { get; init; } =
        [SearchRetrievalSource.Keyword];

    public double? KeywordScore { get; init; }

    public double? FusionScore { get; init; }

    public SemanticMatchView? SemanticMatch { get; init; }

    [JsonIgnore]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SearchPage(
    IReadOnlyList<SearchHit> Hits,
    SearchCoverage Coverage,
    string? NextCursor)
{
    public SearchMode Mode { get; init; } = SearchMode.Keyword;

    public bool Degraded { get; init; }

    public string? DegradedReason { get; init; }
}

public enum SearchRetrievalSource
{
    Keyword,
    Semantic
}

public sealed record SemanticMatchView(
    IReadOnlyList<string> HeadingPath,
    string Excerpt,
    double CosineScore);
