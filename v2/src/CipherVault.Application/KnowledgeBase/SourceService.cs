using CipherVault.Application.Vault;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class SourceService(VaultSessionManager sessionManager)
{
    public Task<PagedResult<SourceSummary>> ListAsync(
        SourceQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await SourceStore.ListAsync(
                context,
                query,
                token),
            cancellationToken).AsTask();

    public Task<SourceDetail> GetAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await SourceStore.GetAsync(
                context,
                sourceId,
                token),
            cancellationToken).AsTask();

    public Task<SourceRevisionSummary> GetRevisionAsync(
        Guid sourceId,
        int revision,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await SourceStore.GetRevisionAsync(
                context,
                sourceId,
                revision,
                token),
            cancellationToken).AsTask();

    public Task<PagedResult<SourceRevisionSummary>> ListRevisionsAsync(
        Guid sourceId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await SourceStore.ListRevisionsAsync(
                context,
                sourceId,
                pageSize,
                cursor,
                token),
            cancellationToken).AsTask();

    public Task<SourceContentChunk> ReadContentAsync(
        Guid sourceId,
        int? revision,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await SourceStore.ReadContentAsync(
                context,
                sourceId,
                revision,
                offset,
                maxChars,
                token),
            cancellationToken).AsTask();

    public Task<PagedResult<SourceLocatorView>> ListLocatorsAsync(
        Guid sourceId,
        int revision,
        SourceLocatorType? type,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await SourceStore.ListLocatorsAsync(
                context,
                sourceId,
                revision,
                type,
                pageSize,
                cursor,
                token),
            cancellationToken).AsTask();

    public Task<SourceLocatorContent> ReadLocatorAsync(
        Guid sourceId,
        int revision,
        Guid locatorId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await SourceStore.ReadLocatorAsync(
                context,
                sourceId,
                revision,
                locatorId,
                offset,
                maxChars,
                token),
            cancellationToken).AsTask();
}
