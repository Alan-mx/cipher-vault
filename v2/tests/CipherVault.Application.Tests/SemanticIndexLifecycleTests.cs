using CipherVault.Application.Semantic;
using CipherVault.Core.Documents;
using CipherVault.Core.Semantic;
using CipherVault.Core.Search;
using CipherVault.Core.Storage.Semantic;

namespace CipherVault.Application.Tests;

public sealed class SemanticIndexLifecycleTests
{
    [Fact]
    public async Task CreateUpdateDeleteRestoreAndPurgeKeepOutboxAndVectorsConsistent()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var provider = new LifecycleProvider();
        var index = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var processor = new SemanticIndexProcessor(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        var semanticSearch = new SemanticSearchService(
            fixture.Manager,
            index,
            fixture.TimeProvider);
        await index.EnsureProfileAsync();
        var status = await index.GetStatusAsync(user);
        var profileId = status.Profile!.ProfileId;
        await SetReadyAsync(fixture, profileId);

        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Lifecycle",
                "notes/lifecycle.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Lifecycle\n\nInitial body.",
                []),
            user);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(
            ("index", created.Revision),
            await ReadJobAsync(fixture, created.DocumentId, profileId));
        Assert.True(await processor.ProcessOneAsync());
        Assert.True(provider.CallCount > 0);
        Assert.True(await CountChunksAsync(fixture, created.DocumentId, profileId) > 0);
        Assert.True(await HasSemanticHitAsync(semanticSearch, "lifecycle"));

        var callsAfterCreateIndex = provider.CallCount;
        var updated = await fixture.Documents.UpdateAsync(
            created.DocumentId,
            created.Revision,
            new UpdateDocumentRequest(
                "Lifecycle",
                "notes/lifecycle.md",
                "# Lifecycle\n\nUpdated body.",
                []),
            user);
        Assert.Equal(callsAfterCreateIndex, provider.CallCount);
        Assert.Equal(
            ("index", updated.Revision),
            await ReadJobAsync(fixture, created.DocumentId, profileId));
        Assert.True(await processor.ProcessOneAsync());

        var callsAfterUpdateIndex = provider.CallCount;
        var deleted = await fixture.Documents.DeleteAsync(
            created.DocumentId,
            updated.Revision,
            user);
        Assert.Equal(callsAfterUpdateIndex, provider.CallCount);
        Assert.Equal(
            ("delete", deleted.Revision),
            await ReadJobAsync(fixture, created.DocumentId, profileId));
        Assert.False(await HasSemanticHitAsync(semanticSearch, "lifecycle"));
        Assert.True(await processor.ProcessOneAsync());
        Assert.Equal(
            0,
            await CountChunksAsync(fixture, created.DocumentId, profileId));

        var callsAfterDeleteCleanup = provider.CallCount;
        var restored = await fixture.Trash.RestoreAsync(
            created.DocumentId,
            deleted.Revision,
            replacementPath: null,
            user);
        Assert.Equal(callsAfterDeleteCleanup, provider.CallCount);
        Assert.Equal(
            ("index", restored.Revision),
            await ReadJobAsync(fixture, created.DocumentId, profileId));
        Assert.True(await processor.ProcessOneAsync());
        Assert.True(await HasSemanticHitAsync(semanticSearch, "lifecycle"));

        var trashedAgain = await fixture.Documents.DeleteAsync(
            created.DocumentId,
            restored.Revision,
            user);
        var callsAfterRestoreIndex = provider.CallCount;
        ((ManualTimeProvider)fixture.TimeProvider).MoveWallClock(
            TimeSpan.FromDays(31));
        _ = await fixture.Trash.PurgeAsync(
            created.DocumentId,
            trashedAgain.Revision,
            user);
        Assert.Equal(callsAfterRestoreIndex, provider.CallCount);
        Assert.Equal(
            ("purge", trashedAgain.Revision),
            await ReadJobAsync(fixture, created.DocumentId, profileId));
        Assert.True(await processor.ProcessOneAsync());
        Assert.Equal(
            0,
            await CountChunksAsync(fixture, created.DocumentId, profileId));
        Assert.Null(
            await ReadJobAsync(fixture, created.DocumentId, profileId));
    }

    private static async Task SetReadyAsync(
        VaultApplicationFixture fixture,
        string profileId) =>
        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    profileId,
                    SemanticProfileStatus.Ready,
                    null,
                    fixture.TimeProvider.GetUtcNow(),
                    CancellationToken.None);
                return 1;
            });

    private static async Task<(string Type, int Revision)?> ReadJobAsync(
        VaultApplicationFixture fixture,
        Guid documentId,
        string profileId) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT job_type, requested_revision
                    FROM semantic_index_jobs
                    WHERE document_id = $documentId
                      AND profile_id = $profileId;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$documentId",
                    documentId.ToString("D"));
                command.Command.Parameters.AddWithValue(
                    "$profileId",
                    profileId);
                await using var reader =
                    await command.Command.ExecuteReaderAsync();
                return await reader.ReadAsync()
                    ? (reader.GetString(0), reader.GetInt32(1))
                    : ((string Type, int Revision)?)null;
            });

    private static async Task<int> CountChunksAsync(
        VaultApplicationFixture fixture,
        Guid documentId,
        string profileId) =>
        await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT count(*)
                    FROM semantic_chunks
                    WHERE document_id = $documentId
                      AND profile_id = $profileId;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$documentId",
                    documentId.ToString("D"));
                command.Command.Parameters.AddWithValue(
                    "$profileId",
                    profileId);
                return Convert.ToInt32(
                    await command.Command.ExecuteScalarAsync());
            });

    private static async Task<bool> HasSemanticHitAsync(
        SemanticSearchService semanticSearch,
        string query)
    {
        var attempt = await semanticSearch.SearchAsync(
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
        return attempt.IsAvailable && attempt.Result!.Hits.Count > 0;
    }

    private sealed class LifecycleProvider : IEmbeddingProvider
    {
        private int callCount;

        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "lifecycle-embed",
            3,
            2048,
            new string('a', 64),
            IsRemote: false);

        public int CountInputTokens(string input) => input.Length;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                request.Inputs
                    .Select(_ => new[] { 1f, 0f, 0f })
                    .ToArray()));
        }
    }
}
