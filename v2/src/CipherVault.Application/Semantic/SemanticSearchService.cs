using CipherVault.Application.Vault;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Semantic;

namespace CipherVault.Application.Semantic;

public sealed record SemanticSearchAttempt(
    SemanticSearchResult? Result,
    string? UnavailableReason,
    string? ProfileId,
    DateTimeOffset? SnapshotAt)
{
    public bool IsAvailable => Result is not null;
}

public sealed class SemanticSearchService
{
    private const int CandidateLimit = 100;
    private readonly VaultSessionManager sessionManager;
    private readonly SemanticIndexService indexService;
    private readonly TimeProvider timeProvider;

    public SemanticSearchService(
        VaultSessionManager sessionManager,
        SemanticIndexService indexService,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.indexService = indexService ??
            throw new ArgumentNullException(nameof(indexService));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SemanticSearchAttempt> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Semantic search requires a non-empty query.");
        }
        if (indexService.Provider is not { } provider)
        {
            return new SemanticSearchAttempt(
                null,
                "not_configured",
                null,
                null);
        }
        await indexService.EnsureProfileAsync(cancellationToken);
        var profile = await ReadReadyProfileAsync(cancellationToken);
        if (profile.Profile is null)
        {
            return new SemanticSearchAttempt(
                null,
                profile.Reason,
                null,
                null);
        }
        EmbeddingResponse embedding;
        try
        {
            embedding = await provider.EmbedAsync(
                new EmbeddingRequest(
                    EmbeddingInputKind.SearchQuery,
                    [query.Query]),
                cancellationToken);
        }
        catch (EmbeddingProviderException exception)
        {
            return new SemanticSearchAttempt(
                null,
                MapEmbeddingFailure(exception),
                profile.Profile.ProfileId,
                null);
        }
        if (!string.Equals(
                embedding.ProviderId,
                profile.Profile.Provider.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                embedding.ModelId,
                profile.Profile.Provider.ModelId,
                StringComparison.Ordinal)
            || embedding.Vectors.Count != 1
            || embedding.Dimension != profile.Profile.Provider.Dimension)
        {
            return new SemanticSearchAttempt(
                null,
                "provider_invalid_response",
                profile.Profile.ProfileId,
                null);
        }
        float[] vector;
        try
        {
            vector = SemanticVectorCodec.Normalize(embedding.Vectors[0]);
        }
        catch (ArgumentException)
        {
            return new SemanticSearchAttempt(
                null,
                "provider_invalid_response",
                profile.Profile.ProfileId,
                null);
        }
        var snapshotAt = timeProvider.GetUtcNow();
        var result = await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var active = await SemanticProfileStore.GetActiveAsync(
                    context,
                    token);
                if (active?.ProfileId != profile.Profile.ProfileId)
                {
                    return new SemanticSearchResult([], 0, 0, false);
                }
                return await SemanticSearchStore.SearchAsync(
                    context,
                    active,
                    query,
                    vector,
                    CandidateLimit,
                    snapshotAt,
                    token);
            },
            cancellationToken);
        return new SemanticSearchAttempt(
            result,
            null,
            profile.Profile.ProfileId,
            snapshotAt);
    }

    private async Task<(SemanticProfile? Profile, string? Reason)>
        ReadReadyProfileAsync(CancellationToken cancellationToken) =>
        await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var profile = await SemanticProfileStore.GetActiveAsync(
                    context,
                    token);
                if (profile is null)
                {
                    return (null, (string?)"not_configured");
                }
                var consent = profile.Provider.IsRemote
                    ? await SemanticConsentStore.GetAsync(
                        context,
                        profile.ProfileId,
                        token)
                    : null;
                if (profile.Provider.IsRemote
                    && !indexService.RemoteDeploymentAllowed)
                {
                    return (null, (string?)"remote_not_allowed");
                }
                if (profile.Provider.IsRemote && consent is not { RevokedAt: null })
                {
                    return (null, (string?)"remote_consent_required");
                }
                var reason = profile.Status switch
                {
                    SemanticProfileStatus.Ready => null,
                    SemanticProfileStatus.Degraded => null,
                    SemanticProfileStatus.Disabled => "remote_consent_required",
                    SemanticProfileStatus.Building => "index_building",
                    SemanticProfileStatus.Failed => "index_failed",
                    SemanticProfileStatus.Invalid => "profile_invalid",
                    _ => "not_configured"
                };
                return (reason is null ? profile : null, reason);
            },
            cancellationToken);

    public static string MapEmbeddingFailure(
        EmbeddingProviderException exception) =>
        exception.Code switch
        {
            "embedding_provider_timeout" => "provider_timeout",
            "embedding_provider_rate_limited" => "provider_rate_limited",
            "embedding_provider_invalid_response" =>
                "provider_invalid_response",
            "embedding_input_too_large" => "query_too_large",
            "semantic_remote_not_allowed" => "remote_not_allowed",
            _ => "provider_unavailable"
        };
}
