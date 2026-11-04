using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Semantic;

namespace CipherVault.Core.Search;

public static class HybridSearchRanker
{
    public const int RrfK = 60;
    public const int CandidateLimit = 100;

    public static SearchPage BuildSemanticPage(
        SearchQuery query,
        string profileId,
        SemanticSearchResult semantic,
        DateTimeOffset snapshotAt)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(semantic);
        var pageSize = NormalizePageSize(query.Page?.PageSize);
        var fingerprint = BuildFingerprint(
            query,
            SearchMode.Semantic,
            profileId,
            RrfK,
            CandidateLimit,
            pageSize);
        var cursor = DecodeCursor(query.Page?.Cursor, fingerprint);
        var hits = semantic.Hits
            .Select(ToSearchHit)
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.UpdatedAt)
            .ThenBy(hit => hit.DocumentId)
            .Where(hit => IsAfterCursor(hit, cursor))
            .ToArray();
        return ToPage(
            hits,
            pageSize,
            SearchMode.Semantic,
            fingerprint,
            snapshotAt,
            new SearchCoverage(
                semantic.ScannedDocuments,
                semantic.ScannedDocuments,
                semantic.MatchedDocuments,
                Math.Min(hits.Length, pageSize),
                semantic.Truncated));
    }

    public static SearchPage BuildHybridPage(
        SearchQuery query,
        string profileId,
        SearchPage keyword,
        SemanticSearchResult semantic,
        DateTimeOffset snapshotAt)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentNullException.ThrowIfNull(semantic);
        var pageSize = NormalizePageSize(query.Page?.PageSize);
        var fingerprint = BuildFingerprint(
            query,
            SearchMode.Hybrid,
            profileId,
            RrfK,
            CandidateLimit,
            pageSize);
        var cursor = DecodeCursor(query.Page?.Cursor, fingerprint);
        var keywordRanks = keyword.Hits
            .Take(CandidateLimit)
            .Select((hit, index) => (hit, rank: index + 1))
            .ToDictionary(item => item.hit.DocumentId);
        var semanticRanks = semantic.Hits
            .Take(CandidateLimit)
            .Select((hit, rank) => (hit, rank: rank + 1))
            .ToDictionary(item => item.hit.DocumentId);
        var ids = keywordRanks.Keys
            .Concat(semanticRanks.Keys)
            .Distinct()
            .ToArray();
        var fused = new List<SearchHit>(ids.Length);
        foreach (var id in ids)
        {
            keywordRanks.TryGetValue(id, out var keywordEntry);
            semanticRanks.TryGetValue(id, out var semanticEntry);
            var keywordHit = keywordEntry.hit;
            var semanticHit = semanticEntry.hit;
            var sources = new List<SearchRetrievalSource>(2);
            var score = 0d;
            if (keywordHit is not null)
            {
                sources.Add(SearchRetrievalSource.Keyword);
                score += 1d / (RrfK + keywordEntry.rank);
            }
            if (semanticHit is not null)
            {
                sources.Add(SearchRetrievalSource.Semantic);
                score += 1d / (RrfK + semanticEntry.rank);
            }
            var metadataHit = keywordHit ?? ToSearchHit(semanticHit!);
            fused.Add(metadataHit with
            {
                Score = score,
                Snippet = semanticHit?.Excerpt ?? keywordHit!.Snippet,
                UpdatedAt = semanticHit?.UpdatedAt
                    ?? keywordHit?.UpdatedAt
                    ?? default,
                RetrievalSources = sources.ToArray(),
                KeywordScore = keywordHit?.Score,
                FusionScore = score,
                SemanticMatch = semanticHit is null
                    ? null
                    : new SemanticMatchView(
                        semanticHit.HeadingPath,
                        semanticHit.Excerpt,
                        semanticHit.CosineScore)
            });
        }

        var ordered = fused
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.UpdatedAt)
            .ThenBy(hit => hit.DocumentId)
            .Where(hit => IsAfterCursor(hit, cursor))
            .ToArray();
        return ToPage(
            ordered,
            pageSize,
            SearchMode.Hybrid,
            fingerprint,
            snapshotAt,
            new SearchCoverage(
                keyword.Coverage.TotalActiveDocuments,
                Math.Min(
                    keyword.Coverage.TotalActiveDocuments,
                    keyword.Coverage.ScannedDocuments + semantic.ScannedDocuments),
                fused.Count,
                Math.Min(ordered.Length, pageSize),
                keyword.Coverage.Truncated || semantic.Truncated));
    }

    public static SearchPage BuildDegradedPage(
        SearchQuery query,
        SearchPage keyword,
        string reason,
        DateTimeOffset snapshotAt)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(keyword);
        var pageSize = NormalizePageSize(query.Page?.PageSize);
        var fingerprint = BuildFingerprint(
            query,
            SearchMode.Hybrid,
            "degraded",
            RrfK,
            CandidateLimit,
            pageSize);
        var cursor = DecodeCursor(query.Page?.Cursor, fingerprint);
        var ordered = keyword.Hits
            .Where(hit => IsAfterCursor(hit, cursor))
            .ToArray();
        var page = ordered.Take(pageSize).ToArray();
        var hasMore = ordered.Length > pageSize;
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "search",
                fingerprint,
                new RankedCursor(
                    page[^1].Score,
                    page[^1].UpdatedAt,
                    page[^1].DocumentId,
                    snapshotAt))
            : null;
        return new SearchPage(
            page,
            keyword.Coverage with
            {
                ReturnedDocuments = page.Length,
                Truncated = keyword.Coverage.Truncated
                    || keyword.NextCursor is not null
                    || hasMore
            },
            nextCursor)
        {
            Mode = SearchMode.Hybrid,
            Degraded = true,
            DegradedReason = reason
        };
    }

    private static SearchPage ToPage(
        IReadOnlyList<SearchHit> hits,
        int pageSize,
        SearchMode mode,
        string fingerprint,
        DateTimeOffset snapshotAt,
        SearchCoverage coverage)
    {
        var page = hits.Take(pageSize).ToArray();
        var hasMore = hits.Count > pageSize;
        var nextCursor = hasMore
            ? CursorCodec.Encode(
                "search",
                fingerprint,
                new RankedCursor(
                    page[^1].Score,
                    page[^1].UpdatedAt,
                    page[^1].DocumentId,
                    snapshotAt))
            : null;
        return new SearchPage(
            page,
            coverage with { ReturnedDocuments = page.Length },
            nextCursor)
        {
            Mode = mode
        };
    }

    private static SearchHit ToSearchHit(SemanticSearchHit hit) =>
        new(
            hit.DocumentId,
            hit.Title,
            hit.VirtualPath,
            hit.Revision,
            hit.Excerpt,
            hit.CosineScore)
        {
            UpdatedAt = hit.UpdatedAt,
            RetrievalSources = [SearchRetrievalSource.Semantic],
            SemanticMatch = new SemanticMatchView(
                hit.HeadingPath,
                hit.Excerpt,
                hit.CosineScore)
        };

    private static RankedCursor? DecodeCursor(
        string? value,
        string fingerprint)
    {
        if (value is null)
        {
            return null;
        }
        if (!CursorCodec.TryDecode(
                value,
                "search",
                fingerprint,
                out RankedCursor? cursor))
        {
            throw new VaultException(
                VaultErrorCode.InvalidCursor,
                "Search cursor is invalid.");
        }
        return cursor;
    }

    private static bool IsAfterCursor(
        SearchHit hit,
        RankedCursor? cursor) =>
        cursor is null
        || hit.Score < cursor.Score
        || hit.Score == cursor.Score
        && hit.UpdatedAt < cursor.UpdatedAt
        || hit.Score == cursor.Score
        && hit.UpdatedAt == cursor.UpdatedAt
        && hit.DocumentId.CompareTo(cursor.DocumentId) > 0;

    private static string BuildFingerprint(
        SearchQuery query,
        SearchMode mode,
        string profileId,
        int rrfK,
        int candidateLimit,
        int pageSize)
    {
        var normalizedQuery = (query.Query ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormC)
            .Trim();
        var tags = TagNormalizer.NormalizeMany(query.Tags);
        var properties = SearchPropertyFilterNormalizer.Normalize(
            query.PropertyFilters);
        var propertyFingerprint =
            SearchPropertyFilterNormalizer.BuildFingerprintKey(
                properties,
                query.PropertyMatch);
        var filterFingerprint = CursorCodec.Fingerprint(
            normalizedQuery,
            mode.ToString(),
            profileId,
            string.Join('\u001F', tags.Select(tag => tag.NormalizedKey)),
            query.Format?.ToDatabaseValue(),
            query.UpdatedAfter?.ToUniversalTime().ToString("O"),
            query.UpdatedBefore?.ToUniversalTime().ToString("O"),
            query.HasBacklinks?.ToString(),
            query.OrphanOnly.ToString(),
            propertyFingerprint,
            rrfK.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return CursorCodec.Fingerprint("search-ranked", filterFingerprint);
    }

    private static int NormalizePageSize(int? value)
    {
        var pageSize = value ?? 20;
        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Search page size must be between 1 and 100.");
        }
        return pageSize;
    }

    private sealed record RankedCursor(
        double Score,
        DateTimeOffset UpdatedAt,
        Guid DocumentId,
        DateTimeOffset SnapshotAt);
}
