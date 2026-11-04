using CipherVault.Application.Semantic;
using CipherVault.Core.Documents;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Semantic;

namespace CipherVault.Application.Tests;

public sealed class SemanticIndexProcessorTests
{
    [Fact]
    public async Task ProcessorBackfillsExistingDocumentAndMarksIndexReady()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Indexed",
                "notes/indexed.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\nproject: CipherVault\n---\n\n# Indexed\n\nBody.",
                []),
            user);
        var provider = new FakeEmbeddingProvider(block: false);
        var service = new SemanticIndexService(
            fixture.Manager,
            provider,
            profileVersion: 1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var processor = new SemanticIndexProcessor(
            fixture.Manager,
            service,
            fixture.TimeProvider);

        await service.EnsureProfileAsync();
        var before = await service.GetStatusAsync(user);
        Assert.True(
            before.PendingJobCount > 0,
            $"state={before.State}, jobs={before.PendingJobCount}");
        var processed = await processor.ProcessOneAsync();
        var status = await service.GetStatusAsync(user);

        Assert.True(processed);
        Assert.Equal(SemanticIndexState.Ready, status.State);
        Assert.Equal(1, status.DocumentCount);
        Assert.True(status.ChunkCount > 0);
        Assert.Equal(0, status.PendingJobCount);
        Assert.NotNull(status.Profile);
        var chunks = await ReadChunksAsync(
            fixture,
            document.DocumentId,
            status.Profile.ProfileId);
        Assert.All(chunks, chunk => Assert.Equal(1, chunk.Revision));
    }

    [Fact]
    public async Task UpdateDuringEmbeddingSupersedesOldResultAndIndexesLatestRevision()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Race",
                "notes/race.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Race\n\nFirst.",
                []),
            user);
        var provider = new FakeEmbeddingProvider(block: true);
        var service = new SemanticIndexService(
            fixture.Manager,
            provider,
            profileVersion: 1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var processor = new SemanticIndexProcessor(
            fixture.Manager,
            service,
            fixture.TimeProvider);
        await service.EnsureProfileAsync();

        var processing = processor.ProcessOneAsync();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var updated = await fixture.Documents.UpdateAsync(
            document.DocumentId,
            document.Revision,
            new UpdateDocumentRequest(
                "Race",
                "notes/race.md",
                "# Race\n\nSecond.",
                []),
            user);
        provider.Release.TrySetResult();
        Assert.True(await processing);

        var statusBetween = await service.GetStatusAsync(user);
        Assert.NotNull(statusBetween.Profile);
        Assert.Equal(0, await CountRunningJobsAsync(fixture));
        var betweenChunks = await ReadChunksAsync(
            fixture,
            document.DocumentId,
            statusBetween.Profile.ProfileId);
        Assert.DoesNotContain(betweenChunks, chunk => chunk.Revision == 1);

        Assert.True(await processor.ProcessOneAsync());
        var status = await service.GetStatusAsync(user);
        Assert.Equal(SemanticIndexState.Ready, status.State);
        var chunks = await ReadChunksAsync(
            fixture,
            document.DocumentId,
            status.Profile!.ProfileId);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Equal(updated.Revision, chunk.Revision));
    }

    [Fact]
    public async Task SessionLockCancellationLeavesNoPartialVectorsAndResumes()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var provider = new CancellableEmbeddingProvider();
        var service = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var processor = new SemanticIndexProcessor(
            fixture.Manager,
            service,
            fixture.TimeProvider);
        await service.EnsureProfileAsync();
        var status = await service.GetStatusAsync(user);
        var profileId = status.Profile!.ProfileId;
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
                return 1;
            });
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Interrupted",
                "notes/interrupted.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Interrupted\n\nBody.",
                []),
            user);
        using var cancellation = new CancellationTokenSource();

        var processing = processor.ProcessOneAsync(cancellation.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = await fixture.Manager.LockAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processing);

        _ = await fixture.UnlockAgainAsync();
        Assert.Equal(
            0,
            await CountChunksAsync(fixture, document.DocumentId, profileId));
        await processor.RecoverInterruptedAsync();
        provider.Block = false;
        Assert.True(await processor.ProcessOneAsync());
        Assert.True(
            await CountChunksAsync(fixture, document.DocumentId, profileId) > 0);
    }

    [Fact]
    public async Task ProcessorHonorsProviderBatchLimit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var provider = new BatchLimitedProvider();
        var service = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: false,
            endpointDisplay: "http://127.0.0.1:1234/v1",
            fixture.TimeProvider);
        var processor = new SemanticIndexProcessor(
            fixture.Manager,
            service,
            fixture.TimeProvider);
        await service.EnsureProfileAsync();
        var status = await service.GetStatusAsync(user);
        var profileId = status.Profile!.ProfileId;
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
                return 1;
            });
        var content = string.Concat(
            Enumerable.Range(1, 5).Select(index =>
                $"# Section {index}\n\n"
                + string.Concat(Enumerable.Repeat("body ", 260))
                + "\n\n"));
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Batch",
                "notes/batch.md",
                null,
                VaultDocumentFormat.Markdown,
                content,
                []),
            user);

        Assert.True(await processor.ProcessOneAsync());

        Assert.True(provider.BatchSizes.Count > 1);
        Assert.All(provider.BatchSizes, count => Assert.InRange(count, 1, 2));
    }

    private static async Task<int> CountRunningJobsAsync(
        VaultApplicationFixture fixture)
    {
        var count = 0;
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT count(*)
                    FROM semantic_index_jobs
                    WHERE status = 'running';
                    """;
                count = Convert.ToInt32(
                    await command.Command.ExecuteScalarAsync());
                return 1;
            });
        return count;
    }

    private static async Task<int> CountChunksAsync(
        VaultApplicationFixture fixture,
        Guid documentId,
        string profileId)
    {
        var count = 0;
        _ = await fixture.QueryAsync(
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
                count = Convert.ToInt32(
                    await command.Command.ExecuteScalarAsync());
                return 1;
            });
        return count;
    }

    private static async Task<IReadOnlyList<StoredChunk>> ReadChunksAsync(
        VaultApplicationFixture fixture,
        Guid documentId,
        string profileId)
    {
        var result = new List<StoredChunk>();
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT source_revision, chunk_ordinal
                    FROM semantic_chunks
                    WHERE document_id = $documentId
                      AND profile_id = $profileId
                    ORDER BY chunk_ordinal;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$documentId",
                    documentId.ToString("D"));
                command.Command.Parameters.AddWithValue("$profileId", profileId);
                await using var reader =
                    await command.Command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.Add(new StoredChunk(
                        reader.GetInt32(0),
                        reader.GetInt32(1)));
                }
                return 1;
            });
        return result;
    }

    private sealed record StoredChunk(int Revision, int Ordinal);

    private sealed class FakeEmbeddingProvider(bool block) : IEmbeddingProvider
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "fake-embed",
            3,
            2048,
            new string('a', 64),
            IsRemote: false);

        public int CountInputTokens(string input) => input.Length;

        public async Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            if (block)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                request.Inputs
                    .Select((_, index) => new[]
                    {
                        1f + index,
                        2f + index,
                        3f + index
                    })
                    .ToArray());
        }
    }

    private sealed class CancellableEmbeddingProvider : IEmbeddingProvider
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Block { get; set; } = true;

        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "cancel-embed",
            3,
            2048,
            new string('a', 64),
            IsRemote: false);

        public int CountInputTokens(string input) => input.Length;

        public async Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            if (Block)
            {
                Entered.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            return new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                request.Inputs.Select(_ => new[] { 1f, 0f, 0f }).ToArray());
        }
    }

    private sealed class BatchLimitedProvider : IEmbeddingProvider
    {
        public List<int> BatchSizes { get; } = [];

        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "batch-embed",
            3,
            2048,
            new string('a', 64),
            IsRemote: false)
        {
            MaxBatchSize = 2
        };

        public int CountInputTokens(string input) => input.Length;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            BatchSizes.Add(request.Inputs.Count);
            return Task.FromResult(new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                request.Inputs.Select(_ => new[] { 1f, 0f, 0f }).ToArray()));
        }
    }
}
