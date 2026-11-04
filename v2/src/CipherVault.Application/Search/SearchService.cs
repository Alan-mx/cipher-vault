using CipherVault.Application.Vault;
using CipherVault.Application.Semantic;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Search;

namespace CipherVault.Application.Search;

public sealed class SearchService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly SemanticSearchService? semanticSearch;

    public SearchService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null,
        SemanticSearchService? semanticSearch = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.semanticSearch = semanticSearch;
    }

    public async Task<SearchPage> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        return query.Mode switch
        {
            SearchMode.Keyword => await SearchKeywordAsync(
                query,
                cancellationToken),
            SearchMode.Semantic => await SearchSemanticAsync(
                query,
                cancellationToken),
            SearchMode.Hybrid => await SearchHybridAsync(
                query,
                cancellationToken),
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Search mode is invalid.")
        };
    }

    private Task<SearchPage> SearchKeywordAsync(
        SearchQuery query,
        CancellationToken cancellationToken) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<SearchPage>(
                SearchStore.SearchAsync(
                    context,
                    query with { Mode = SearchMode.Keyword },
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();

    private async Task<SearchPage> SearchSemanticAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        var attempt = await RequireSemantic().SearchAsync(
            query,
            cancellationToken);
        if (!attempt.IsAvailable
            || attempt.Result is null
            || attempt.ProfileId is null
            || attempt.SnapshotAt is null)
        {
            throw SemanticUnavailable(attempt.UnavailableReason);
        }
        return HybridSearchRanker.BuildSemanticPage(
            query,
            attempt.ProfileId,
            attempt.Result,
            attempt.SnapshotAt.Value);
    }

    private async Task<SearchPage> SearchHybridAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        var attempt = await RequireSemantic().SearchAsync(
            query,
            cancellationToken);
        if (!attempt.IsAvailable
            || attempt.Result is null
            || attempt.ProfileId is null
            || attempt.SnapshotAt is null)
        {
            if (attempt.UnavailableReason == "query_too_large")
            {
                throw SemanticUnavailable(attempt.UnavailableReason);
            }
            var fallback = await SearchKeywordAsync(
                query with
                {
                    Page = new CursorPageRequest(
                        HybridSearchRanker.CandidateLimit,
                        Cursor: null)
                },
                cancellationToken);
            return HybridSearchRanker.BuildDegradedPage(
                query,
                fallback,
                attempt.UnavailableReason ?? "provider_unavailable",
                timeProvider.GetUtcNow());
        }

        var candidateQuery = query with
        {
            Mode = SearchMode.Keyword,
            Page = new CursorPageRequest(
                HybridSearchRanker.CandidateLimit,
                Cursor: null)
        };
        var keyword = await SearchKeywordAsync(
            candidateQuery,
            cancellationToken);
        return HybridSearchRanker.BuildHybridPage(
            query,
            attempt.ProfileId,
            keyword,
            attempt.Result,
            attempt.SnapshotAt.Value);
    }

    private SemanticSearchService RequireSemantic() =>
        semanticSearch
        ?? throw SemanticUnavailable("not_configured");

    private static VaultException SemanticUnavailable(string? reason) =>
        reason == "query_too_large"
            ? new VaultException(
                VaultErrorCode.InvalidInput,
                "Semantic search query exceeds the active profile input limit.")
            : reason == "remote_not_allowed"
            ? new VaultException(
                VaultErrorCode.SemanticRemoteNotAllowed,
                "Remote semantic embedding is not allowed.")
            : new VaultException(
                VaultErrorCode.SemanticSearchUnavailable,
                "Semantic search is unavailable.",
                details: new Dictionary<string, object?>
                {
                    ["reasonCode"] = reason ?? "provider_unavailable"
                });
}
