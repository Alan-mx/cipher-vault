using CipherVault.Application.Semantic;
using CipherVault.Application.Search;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Semantic;
using CipherVault.Core.Search;
using CipherVault.Core.Storage.Semantic;
using System.Text.Json;

namespace CipherVault.Application.Tests;

public sealed class SemanticSearchServiceTests
{
    [Fact]
    public async Task SemanticSearchUsesBestChunkAndSharedFilters()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        const string redisContent =
            "---\nstatus: raw\n---\n\n# Cache\n\nRedis body.";
        const string otherContent =
            "---\nstatus: verified\n---\n\n# Other\n\nOther body.";
        var redis = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Redis Cache",
                "notes/redis-cache.md",
                null,
                VaultDocumentFormat.Markdown,
                redisContent,
                ["redis"]),
            user);
        var other = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Other Cache",
                "notes/other-cache.md",
                null,
                VaultDocumentFormat.Markdown,
                otherContent,
                ["other"]),
            user);
        var redisStored = await fixture.Documents.ReadContentAsync(
            redis.DocumentId,
            0,
            65_536);
        var otherStored = await fixture.Documents.ReadContentAsync(
            other.DocumentId,
            0,
            65_536);
        var provider = new QueryEmbeddingProvider();
        var index = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var semantic = new SemanticSearchService(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        var status = await index.GetStatusAsync(user);
        Assert.NotNull(status.Profile);
        await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    status.Profile.ProfileId,
                    SemanticProfileStatus.Ready,
                    null,
                    fixture.TimeProvider.GetUtcNow(),
                    CancellationToken.None);
                await SemanticChunkStore.ReplaceDocumentAsync(
                    context,
                    redis.DocumentId,
                    status.Profile.ProfileId,
                    redis.Revision,
                    3,
                    [
                        Chunk(
                            redis.DocumentId,
                            status.Profile.ProfileId,
                            redis.Revision,
                            new[] { 1f, 0f, 0f },
                            redisStored.Content.IndexOf(
                                "Redis body.",
                                StringComparison.Ordinal),
                            "Redis body.")
                    ],
                    CancellationToken.None);
                await SemanticChunkStore.ReplaceDocumentAsync(
                    context,
                    other.DocumentId,
                    status.Profile.ProfileId,
                    other.Revision,
                    3,
                    [
                        Chunk(
                            other.DocumentId,
                            status.Profile.ProfileId,
                            other.Revision,
                            new[] { 0f, 1f, 0f },
                            otherStored.Content.IndexOf(
                                "Other body.",
                                StringComparison.Ordinal),
                            "Other body.")
                    ],
                    CancellationToken.None);
                return 1;
            });

        var attempt = await semantic.SearchAsync(
            new SearchQuery(
                "cache",
                SearchMode.Semantic,
                ["redis"],
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest(10, null)));

        Assert.True(attempt.IsAvailable);
        var hit = Assert.Single(attempt.Result!.Hits);
        Assert.Equal(redis.DocumentId, hit.DocumentId);
        Assert.Equal(["Cache"], hit.HeadingPath);
        Assert.Equal("Redis body.", hit.Excerpt);
    }

    [Fact]
    public async Task KeywordDoesNotCallProviderAndHybridReportsStableDegradation()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var provider = new TimeoutEmbeddingProvider();
        var index = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var semantic = new SemanticSearchService(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        var search = new SearchService(
            fixture.Manager,
            fixture.TimeProvider,
            semantic);
        await index.EnsureProfileAsync();
        var status = await index.GetStatusAsync(user);
        await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    status.Profile!.ProfileId,
                    SemanticProfileStatus.Ready,
                    null,
                    fixture.TimeProvider.GetUtcNow(),
                    CancellationToken.None);
                return 1;
            });

        _ = await search.SearchAsync(
            Query("anything", SearchMode.Keyword));
        Assert.Equal(0, provider.CallCount);

        var semanticError = await Assert.ThrowsAsync<VaultException>(() =>
            search.SearchAsync(Query("anything", SearchMode.Semantic)));
        Assert.Equal(
            VaultErrorCode.SemanticSearchUnavailable,
            semanticError.Code);
        Assert.Equal(
            "provider_timeout",
            semanticError.Details["reasonCode"]);

        var hybrid = await search.SearchAsync(
            Query("anything", SearchMode.Hybrid));
        Assert.Equal(SearchMode.Hybrid, hybrid.Mode);
        Assert.True(hybrid.Degraded);
        Assert.Equal("provider_timeout", hybrid.DegradedReason);
    }

    [Fact]
    public async Task RemoteProviderDoesNotReceiveRequestsBeforeWebConsent()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var provider = new RemoteCountingProvider();
        var index = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: true,
            endpointDisplay: "https://embedding.example.com/v1",
            fixture.TimeProvider);
        var semantic = new SemanticSearchService(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        var processor = new SemanticIndexProcessor(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Remote",
                "notes/remote.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Remote\n\nBody.",
                []),
            user);
        await index.EnsureProfileAsync();
        Assert.False(await processor.ProcessOneAsync());

        var error = await Assert.ThrowsAsync<VaultException>(() =>
            new SearchService(
                fixture.Manager,
                fixture.TimeProvider,
                semantic).SearchAsync(
                Query("remote query", SearchMode.Semantic)));

        Assert.Equal(
            VaultErrorCode.SemanticSearchUnavailable,
            error.Code);
        Assert.Equal(
            "remote_consent_required",
            error.Details["reasonCode"]);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task RemoteDeploymentOptOutReturnsDedicatedStableError()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var provider = new RemoteCountingProvider();
        var index = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "https://embedding.example.com/v1",
            fixture.TimeProvider);
        var semantic = new SemanticSearchService(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        await index.EnsureProfileAsync();
        var search = new SearchService(
            fixture.Manager,
            fixture.TimeProvider,
            semantic);

        var error = await Assert.ThrowsAsync<VaultException>(() =>
            search.SearchAsync(
                Query("remote query", SearchMode.Semantic)));

        Assert.Equal(
            VaultErrorCode.SemanticRemoteNotAllowed,
            error.Code);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SemanticSearchAppliesTagsFormatTimeBacklinkAndProperties()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var manual = (ManualTimeProvider)fixture.TimeProvider;
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var old = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Old Target",
                "notes/old.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\nstatus: verified\n---\n\n# Old",
                ["target"]),
            user);
        manual.MoveWallClock(TimeSpan.FromHours(2));
        var wrongProperty = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Wrong Property",
                "notes/wrong-property.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\nstatus: draft\n---\n\n# Wrong",
                ["target"]),
            user);
        var wrongTag = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Wrong Tag",
                "notes/wrong-tag.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\nstatus: verified\n---\n\n# Wrong",
                ["other"]),
            user);
        var wrongFormat = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Wrong Format",
                "notes/wrong-format.txt",
                null,
                VaultDocumentFormat.Text,
                "status: verified\n\nWrong",
                ["target"]),
            user);
        var target = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Filter Target",
                "notes/target.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\nstatus: verified\n---\n\n# Target",
                ["target"]),
            user);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Backlink Source",
                "notes/source.md",
                null,
                VaultDocumentFormat.Markdown,
                "[[Filter Target]]",
                ["source"]),
            user);
        var after = manual.GetUtcNow();
        var provider = new QueryVectorProvider([1f, 0f, 0f]);
        var index = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var semantic = new SemanticSearchService(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        await index.EnsureProfileAsync();
        var status = await index.GetStatusAsync(user);
        var profileId = status.Profile!.ProfileId;
        await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    profileId,
                    SemanticProfileStatus.Ready,
                    null,
                    manual.GetUtcNow(),
                    CancellationToken.None);
                foreach (var document in new[]
                         {
                             old,
                             wrongProperty,
                             wrongTag,
                             wrongFormat,
                             target
                         })
                {
                    await SemanticChunkStore.ReplaceDocumentAsync(
                        context,
                        document.DocumentId,
                        profileId,
                        document.Revision,
                        3,
                        [
                            Chunk(
                                document.DocumentId,
                                profileId,
                                document.Revision,
                                [1f, 0f, 0f],
                                0,
                                "#")
                        ],
                        CancellationToken.None);
                }
                return 1;
            });
        using var value = JsonDocument.Parse("\"verified\"");
        var query = new SearchQuery(
            "target",
            SearchMode.Semantic,
            ["target"],
            VaultDocumentFormat.Markdown,
            after.AddMinutes(-1),
            after.AddMinutes(1),
            true,
            false,
            new CursorPageRequest(20),
            [
                new SearchPropertyFilter(
                    "status",
                    SearchPropertyOperator.Eq,
                    value.RootElement.Clone())
            ]);

        var attempt = await semantic.SearchAsync(query);

        Assert.True(attempt.IsAvailable);
        Assert.Equal(
            target.DocumentId,
            Assert.Single(attempt.Result!.Hits).DocumentId);
    }

    private static SemanticChunkRecord Chunk(
        Guid documentId,
        string profileId,
        int revision,
        float[] vector,
        int startOffset,
        string content)
    {
        var encoded = SemanticVectorCodec.NormalizeAndEncode(vector);
        return new SemanticChunkRecord(
            documentId,
            profileId,
            revision,
            0,
            ["Cache"],
            startOffset,
            startOffset + content.Length,
            SemanticProfileIdentity.Sha256Hex(content),
            vector.Length,
            encoded,
            DateTimeOffset.Parse("2026-11-05T00:00:00Z"));
    }

    private static SearchQuery Query(string value, SearchMode mode) =>
        new(
            value,
            mode,
            null,
            null,
            null,
            null,
            null,
            false,
            new CursorPageRequest(20));

    private sealed class QueryEmbeddingProvider : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "query-embed",
            3,
            2048,
            new string('a', 64),
            IsRemote: false);

        public int CountInputTokens(string input) => input.Length;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                [[1f, 0f, 0f]]));
    }

    private sealed class QueryVectorProvider(float[] vector) : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "query-vector",
            vector.Length,
            2048,
            new string('a', 64),
            IsRemote: false);

        public int CountInputTokens(string input) => input.Length;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                [vector]));
    }

    private sealed class TimeoutEmbeddingProvider : IEmbeddingProvider
    {
        private int callCount;

        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "timeout-embed",
            3,
            2048,
            new string('a', 64),
            IsRemote: false);

        public int CallCount => Volatile.Read(ref callCount);

        public int CountInputTokens(string input) => input.Length;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            throw new EmbeddingProviderException(
                "embedding_provider_timeout",
                "Embedding provider timed out.");
        }
    }

    private sealed class RemoteCountingProvider : IEmbeddingProvider
    {
        private int callCount;

        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "remote-embed",
            3,
            2048,
            new string('b', 64),
            IsRemote: true);

        public int CallCount => Volatile.Read(ref callCount);

        public int CountInputTokens(string input) => input.Length;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                request.Inputs.Select(_ => new[] { 1f, 0f, 0f }).ToArray()));
        }
    }
}
