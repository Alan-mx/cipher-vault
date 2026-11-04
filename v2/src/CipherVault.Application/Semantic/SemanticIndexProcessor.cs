using CipherVault.Application.Vault;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Semantic;

namespace CipherVault.Application.Semantic;

public sealed class SemanticIndexProcessor
{
    private readonly VaultSessionManager sessionManager;
    private readonly SemanticIndexService indexService;
    private readonly TimeProvider timeProvider;

    public SemanticIndexProcessor(
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

    public async Task<bool> ProcessOneAsync(
        CancellationToken cancellationToken = default)
    {
        if (indexService.Provider is not { } provider)
        {
            return false;
        }
        await indexService.EnsureProfileAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        SemanticWorkItem? item;
        try
        {
            item = await sessionManager.ExecuteAsync(
                async (context, token) =>
                {
                    await context.BeginTransactionAsync(token);
                    try
                    {
                        var active = await SemanticProfileStore.GetActiveAsync(
                            context,
                            token);
                        if (active is null
                            || active.Status == SemanticProfileStatus.Disabled)
                        {
                            await context.CommitTransactionAsync(token);
                            return null;
                        }
                        var job = await SemanticIndexJobStore.ClaimNextAsync(
                            context,
                            now,
                            token);
                        SemanticDocumentSnapshot? snapshot = null;
                        if (job is { Type: SemanticIndexJobType.Index })
                        {
                            snapshot =
                                await SemanticIndexDocumentStore.ReadCurrentAsync(
                                    context,
                                    job.DocumentId,
                                    token);
                        }
                        await context.CommitTransactionAsync(token);
                        return job is null
                            ? null
                            : new SemanticWorkItem(job, snapshot);
                    }
                    catch
                    {
                        await context.RollbackTransactionAsync(token);
                        throw;
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        if (item is null)
        {
            return false;
        }

        if (item.Job.Type is SemanticIndexJobType.Delete
            or SemanticIndexJobType.Purge)
        {
            await ProcessCleanupAsync(item.Job, cancellationToken);
            return true;
        }
        if (item.Snapshot is null)
        {
            await CompleteEmptyIndexAsync(item.Job, cancellationToken);
            return true;
        }

        try
        {
            var chunks = SemanticTextChunker.Chunk(
                new SemanticChunkingRequest(
                    item.Snapshot.DocumentId,
                    item.Snapshot.Revision,
                    item.Snapshot.Title,
                    item.Snapshot.Format,
                    item.Snapshot.Content,
                    item.Snapshot.Tags,
                    item.Snapshot.ContextFields),
                provider.Descriptor.MaxInputTokens,
                provider.CountInputTokens);
            var records = new List<SemanticChunkRecord>(chunks.Count);
            var batchSize = provider.Descriptor.MaxBatchSize;
            if (batchSize is < 1 or > 32)
            {
                throw new InvalidOperationException(
                    "Embedding provider batch size is invalid.");
            }
            for (var offset = 0;
                 offset < chunks.Count;
                 offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = chunks
                    .Skip(offset)
                    .Take(batchSize)
                    .ToArray();
                var response = await provider.EmbedAsync(
                    new EmbeddingRequest(
                        EmbeddingInputKind.DocumentChunk,
                        batch.Select(chunk => chunk.ProviderInput).ToArray()),
                    cancellationToken);
                ValidateEmbeddingResponse(response, batch.Length, provider.Descriptor);
                for (var index = 0; index < batch.Length; index++)
                {
                    var chunk = batch[index];
                    var vector = SemanticVectorCodec.NormalizeAndEncode(
                        response.Vectors[index]);
                    records.Add(new SemanticChunkRecord(
                        item.Snapshot.DocumentId,
                        item.Job.ProfileId,
                        item.Snapshot.Revision,
                        chunk.ChunkOrdinal,
                        chunk.HeadingPath,
                        chunk.StartOffset,
                        chunk.EndOffset,
                        chunk.ContentHash,
                        provider.Descriptor.Dimension,
                        vector,
                        now));
                }
            }
            await CompleteIndexAsync(
                item.Job,
                item.Snapshot.ContentHash,
                records,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = exception is EmbeddingProviderException providerException
                ? providerException.Code
                : exception is ArgumentException
                    ? "embedding_input_invalid"
                    : "embedding_index_failed";
            await FailJobAsync(item.Job, code, cancellationToken);
        }
        return true;
    }

    public async Task RecoverInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        _ = await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    _ = await SemanticIndexJobStore.RecoverRunningAsync(
                        context,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return 1;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    private async Task ProcessCleanupAsync(
        SemanticIndexJob job,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        _ = await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var currentJob = await SemanticIndexJobStore.GetAsync(
                        context,
                        job.DocumentId,
                        job.ProfileId,
                        token);
                    if (currentJob is null
                        || currentJob.RequestedRevision != job.RequestedRevision
                        || currentJob.Type != job.Type)
                    {
                        await context.CommitTransactionAsync(token);
                        return 1;
                    }
                    await SemanticChunkStore.DeleteDocumentAsync(
                        context,
                        job.DocumentId,
                        job.ProfileId,
                        token);
                    if (job.Type == SemanticIndexJobType.Purge)
                    {
                        await SemanticIndexJobStore.DeleteAsync(
                            context,
                            job.DocumentId,
                            job.ProfileId,
                            token);
                    }
                    else
                    {
                        await SemanticIndexJobStore.CompleteAsync(
                            context,
                            job.DocumentId,
                            job.ProfileId,
                            now,
                            token);
                    }
                    await RefreshProfileStateAsync(context, job.ProfileId, now, token);
                    await context.CommitTransactionAsync(token);
                    return 1;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    private async Task CompleteEmptyIndexAsync(
        SemanticIndexJob job,
        CancellationToken cancellationToken) =>
        await CompleteIndexAsync(
            job,
            expectedContentHash: null,
            chunks: [],
            cancellationToken);

    private async Task CompleteIndexAsync(
        SemanticIndexJob job,
        string? expectedContentHash,
        IReadOnlyList<SemanticChunkRecord> chunks,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        _ = await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var active = await SemanticProfileStore.GetActiveAsync(
                        context,
                        token);
                    var currentJob = await SemanticIndexJobStore.GetAsync(
                        context,
                        job.DocumentId,
                        job.ProfileId,
                        token);
                    if (active?.ProfileId != job.ProfileId
                        || currentJob is null
                        || currentJob.RequestedRevision != job.RequestedRevision
                        || currentJob.Type != SemanticIndexJobType.Index)
                    {
                        await context.CommitTransactionAsync(token);
                        return 1;
                    }
                    var current =
                        await SemanticIndexDocumentStore.ReadCurrentAsync(
                            context,
                            job.DocumentId,
                            token);
                    if (current is null)
                    {
                        await SemanticChunkStore.DeleteDocumentAsync(
                            context,
                            job.DocumentId,
                            job.ProfileId,
                            token);
                        await SemanticIndexJobStore.CompleteAsync(
                            context,
                            job.DocumentId,
                            job.ProfileId,
                            now,
                            token);
                        await RefreshProfileStateAsync(
                            context,
                            job.ProfileId,
                            now,
                            token);
                        await context.CommitTransactionAsync(token);
                        return 1;
                    }
                    if (current.Revision != job.RequestedRevision
                        || expectedContentHash is not null
                        && !string.Equals(
                            current.ContentHash,
                            expectedContentHash,
                            StringComparison.Ordinal)
                        || job.DocumentContentHash is not null
                        && !string.Equals(
                            current.ContentHash,
                            job.DocumentContentHash,
                            StringComparison.Ordinal))
                    {
                        await SemanticIndexJobStore.RequeueWithLatestDocumentAsync(
                            context,
                            job,
                            current.Revision,
                            current.ContentHash,
                            now,
                            token);
                        await context.CommitTransactionAsync(token);
                        return 1;
                    }
                    var profile = active ?? throw new InvalidOperationException(
                        "Active semantic profile disappeared.");
                    await SemanticChunkStore.ReplaceDocumentAsync(
                        context,
                        job.DocumentId,
                        job.ProfileId,
                        current.Revision,
                        profile.Provider.Dimension,
                        chunks,
                        token);
                    await SemanticIndexJobStore.CompleteAsync(
                        context,
                        job.DocumentId,
                        job.ProfileId,
                        now,
                        token);
                    await RefreshProfileStateAsync(
                        context,
                        job.ProfileId,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return 1;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    private async Task FailJobAsync(
        SemanticIndexJob job,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var delaySeconds = Math.Min(
            900,
            5 * Math.Pow(2, Math.Min(job.AttemptCount, 8)));
        _ = await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var currentJob = await SemanticIndexJobStore.GetAsync(
                        context,
                        job.DocumentId,
                        job.ProfileId,
                        token);
                    if (currentJob is null
                        || currentJob.RequestedRevision != job.RequestedRevision)
                    {
                        await context.CommitTransactionAsync(token);
                        return 1;
                    }
                    await SemanticIndexJobStore.FailAsync(
                        context,
                        job.DocumentId,
                        job.ProfileId,
                        errorCode,
                        now.AddSeconds(delaySeconds),
                        now,
                        token);
                    var chunks = await SemanticChunkStore.CountAsync(
                        context,
                        job.ProfileId,
                        token);
                    await SemanticProfileStore.UpdateStatusAsync(
                        context,
                        job.ProfileId,
                        chunks > 0
                            ? SemanticProfileStatus.Degraded
                            : SemanticProfileStatus.Failed,
                        errorCode,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return 1;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    private static async Task RefreshProfileStateAsync(
        VaultOperationContext context,
        string profileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profile = await SemanticProfileStore.FindAsync(
            context,
            profileId,
            cancellationToken);
        if (profile is null
            || profile.Status is not (
                SemanticProfileStatus.Building
                or SemanticProfileStatus.Degraded))
        {
            return;
        }
        var counts = await SemanticIndexJobStore.GetCountsAsync(
            context,
            profileId,
            cancellationToken);
        var chunks = await SemanticChunkStore.CountAsync(
            context,
            profileId,
            cancellationToken);
        var status = counts.Pending + counts.Running + counts.Failed == 0
            ? SemanticProfileStatus.Ready
            : counts.Failed > 0
                ? chunks > 0
                    ? SemanticProfileStatus.Degraded
                    : SemanticProfileStatus.Failed
                : SemanticProfileStatus.Building;
        await SemanticProfileStore.UpdateStatusAsync(
            context,
            profileId,
            status,
            counts.Failed > 0 ? profile.LastErrorCode : null,
            now,
            cancellationToken);
    }

    private static void ValidateEmbeddingResponse(
        EmbeddingResponse response,
        int expectedCount,
        EmbeddingProviderDescriptor descriptor)
    {
        if (response.Vectors.Count != expectedCount
            || response.Dimension != descriptor.Dimension
            || !string.Equals(
                response.ProviderId,
                descriptor.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                response.ModelId,
                descriptor.ModelId,
                StringComparison.Ordinal))
        {
            throw new EmbeddingProviderException(
                "embedding_provider_invalid_response",
                "Embedding response does not match the active profile.");
        }
        try
        {
            foreach (var vector in response.Vectors)
            {
                _ = SemanticVectorCodec.Normalize(vector);
            }
        }
        catch (ArgumentException exception)
        {
            throw new EmbeddingProviderException(
                "embedding_provider_invalid_response",
                "Embedding response contains an invalid vector.",
                exception);
        }
    }

    private sealed record SemanticWorkItem(
        SemanticIndexJob Job,
        SemanticDocumentSnapshot? Snapshot);
}
