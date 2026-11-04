using CipherVault.Application.Semantic;
using CipherVault.Core.Documents;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Semantic;

namespace CipherVault.Application.Tests;

public sealed class SemanticSearchLanguageTests
{
    [Theory]
    [InlineData("缓存一致性", 0)]
    [InlineData("cache coherence", 1)]
    [InlineData("缓存 cache", 2)]
    public async Task ChineseEnglishAndMixedQueriesSelectExpectedDocument(
        string query,
        int expectedIndex)
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var documents = new[]
        {
            await CreateAsync(
                fixture,
                user,
                "Chinese",
                "notes/chinese.md",
                "# 中文\n\n缓存一致性正文。"),
            await CreateAsync(
                fixture,
                user,
                "English",
                "notes/english.md",
                "# English\n\nCache coherence body."),
            await CreateAsync(
                fixture,
                user,
                "Mixed",
                "notes/mixed.md",
                "# Mixed\n\n缓存 cache body.")
        };
        var provider = new LanguageProvider();
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
        var storedDocuments = new List<DocumentContentChunk>();
        foreach (var document in documents)
        {
            storedDocuments.Add(
                await fixture.Documents.ReadContentAsync(
                    document.DocumentId,
                    0,
                    65_536));
        }
        await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    profileId,
                    SemanticProfileStatus.Ready,
                    null,
                    fixture.TimeProvider.GetUtcNow(),
                    CancellationToken.None);
                for (var i = 0; i < documents.Length; i++)
                {
                    var document = documents[i];
                    var stored = storedDocuments[i];
                    var start = stored.Content.IndexOf('\n', 0) + 1;
                    var excerpt = stored.Content[start..].Trim();
                    await SemanticChunkStore.ReplaceDocumentAsync(
                        context,
                        document.DocumentId,
                        profileId,
                        document.Revision,
                        3,
                        [
                            new SemanticChunkRecord(
                                document.DocumentId,
                                profileId,
                                document.Revision,
                                0,
                                ["Language"],
                                start,
                                stored.Content.Length,
                                SemanticProfileIdentity.Sha256Hex(excerpt),
                                3,
                                SemanticVectorCodec.NormalizeAndEncode(
                                    LanguageProvider.DocumentVector(i)),
                                fixture.TimeProvider.GetUtcNow())
                        ],
                        CancellationToken.None);
                }
                return 1;
            });

        var attempt = await semantic.SearchAsync(
            new SearchQuery(
                query,
                SearchMode.Semantic,
                null,
                null,
                null,
                null,
                null,
                false,
                new CursorPageRequest(10)));

        Assert.True(attempt.IsAvailable);
        Assert.Equal(
            documents[expectedIndex].DocumentId,
            attempt.Result!.Hits[0].DocumentId);
    }

    private static async Task<DocumentWriteResult> CreateAsync(
        VaultApplicationFixture fixture,
        VaultActorContext user,
        string title,
        string path,
        string content) =>
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                title,
                path,
                null,
                VaultDocumentFormat.Markdown,
                content,
                []),
            user);

    private sealed class LanguageProvider : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "language-embed",
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
                [VectorForQuery(request.Inputs[0])]));

        public float[] VectorForQuery(string query) =>
            query switch
            {
                "缓存一致性" => [1f, 0f, 0f],
                "cache coherence" => [0f, 1f, 0f],
                "缓存 cache" => [1f, 1f, 0f],
                _ => [1f, 0f, 0f]
            };

        public static float[] DocumentVector(int index) =>
            index switch
            {
                0 => [1f, 0f, 0f],
                1 => [0f, 1f, 0f],
                _ => [1f, 1f, 0f]
            };
    }
}
