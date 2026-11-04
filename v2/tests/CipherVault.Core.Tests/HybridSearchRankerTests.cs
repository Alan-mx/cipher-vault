using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;

namespace CipherVault.Core.Tests;

public sealed class HybridSearchRankerTests
{
    [Fact]
    public void HybridFusionDeduplicatesAndUsesRrfOrdering()
    {
        var now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var query = new SearchQuery(
            "cache",
            SearchMode.Hybrid,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(10, null));
        var keyword = new SearchPage(
            [
                Hit(a, "A", -1, now),
                Hit(b, "B", -2, now.AddMinutes(-1))
            ],
            new SearchCoverage(10, 10, 2, 2, false),
            null);
        var semantic = new SemanticSearchResult(
            [
                Semantic(b, "B", now.AddMinutes(-1), 0.95),
                Semantic(c, "C", now.AddMinutes(-2), 0.9)
            ],
            10,
            2,
            false);

        var page = HybridSearchRanker.BuildHybridPage(
            query,
            new string('a', 64),
            keyword,
            semantic,
            now);

        Assert.Equal(SearchMode.Hybrid, page.Mode);
        Assert.Equal(b, page.Hits[0].DocumentId);
        Assert.Equal(
            [SearchRetrievalSource.Keyword, SearchRetrievalSource.Semantic],
            page.Hits[0].RetrievalSources);
        Assert.Equal(1, page.Hits.Count(hit => hit.DocumentId == b));
        Assert.Equal(2, page.Hits[0].RetrievalSources.Length);
        Assert.Equal(
            1d / (HybridSearchRanker.RrfK + 2)
            + 1d / (HybridSearchRanker.RrfK + 1),
            page.Hits[0].Score,
            precision: 12);
    }

    [Fact]
    public void SemanticPageCursorIsBoundToProfileAndPageSize()
    {
        var now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
        var query = new SearchQuery(
            "cache",
            SearchMode.Semantic,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(1, null));
        var semantic = new SemanticSearchResult(
            [
                Semantic(Guid.NewGuid(), "A", now, 0.9),
                Semantic(Guid.NewGuid(), "B", now.AddMinutes(-1), 0.8)
            ],
            2,
            2,
            false);

        var page = HybridSearchRanker.BuildSemanticPage(
            query,
            new string('a', 64),
            semantic,
            now);
        Assert.NotNull(page.NextCursor);

        var next = HybridSearchRanker.BuildSemanticPage(
            query with
            {
                Page = new CursorPageRequest(1, page.NextCursor)
            },
            new string('a', 64),
            semantic,
            now);
        Assert.Single(next.Hits);
        Assert.NotEqual(page.Hits[0].DocumentId, next.Hits[0].DocumentId);

        var invalid = Assert.Throws<VaultException>(
            () => HybridSearchRanker.BuildSemanticPage(
                query with
                {
                    Page = new CursorPageRequest(2, page.NextCursor)
                },
                new string('a', 64),
                semantic,
                now));
        Assert.Equal(VaultErrorCode.InvalidCursor, invalid.Code);
    }

    [Fact]
    public void DegradedHybridUsesHybridCursorAcrossPages()
    {
        var now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var query = new SearchQuery(
            "cache",
            SearchMode.Hybrid,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(1, null));
        var keyword = new SearchPage(
            [
                Hit(a, "A", -1, now),
                Hit(b, "B", -2, now.AddMinutes(-1))
            ],
            new SearchCoverage(10, 10, 2, 2, false),
            "keyword-only-cursor");

        var first = HybridSearchRanker.BuildDegradedPage(
            query,
            keyword,
            "provider_timeout",
            now);
        var second = HybridSearchRanker.BuildDegradedPage(
            query with
            {
                Page = new CursorPageRequest(1, first.NextCursor)
            },
            keyword,
            "provider_timeout",
            now);

        Assert.Equal(SearchMode.Hybrid, first.Mode);
        Assert.True(first.Degraded);
        Assert.Equal("provider_timeout", first.DegradedReason);
        Assert.Single(first.Hits);
        Assert.Equal(a, first.Hits[0].DocumentId);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(b, Assert.Single(second.Hits).DocumentId);
        Assert.Null(second.NextCursor);
    }

    private static SearchHit Hit(
        Guid id,
        string title,
        double score,
        DateTimeOffset updatedAt) =>
        new(
            id,
            title,
            $"notes/{title}.md",
            1,
            title,
            score)
        {
            UpdatedAt = updatedAt
        };

    private static SemanticSearchHit Semantic(
        Guid id,
        string title,
        DateTimeOffset updatedAt,
        double score) =>
        new(
            id,
            title,
            $"notes/{title}.md",
            1,
            1,
            0,
            [title],
            0,
            title.Length,
            title,
            score,
            updatedAt);
}
