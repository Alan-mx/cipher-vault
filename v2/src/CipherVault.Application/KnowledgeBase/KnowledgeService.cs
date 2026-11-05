using CipherVault.Application.Vault;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class KnowledgeService(VaultSessionManager sessionManager)
{
    public Task<PagedResult<KnowledgeSummary>> ListAsync(
        KnowledgeQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await KnowledgeStore.ListAsync(
                context,
                query,
                token),
            cancellationToken).AsTask();

    public Task<PagedResult<KnowledgeSearchHit>> SearchAsync(
        KnowledgeSearchQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await KnowledgeStore.SearchAsync(
                context,
                query,
                token),
            cancellationToken).AsTask();

    public Task<KnowledgeDetail> GetAsync(
        Guid knowledgeId,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await KnowledgeStore.GetAsync(
                context,
                knowledgeId,
                token),
            cancellationToken).AsTask();

    public Task<PagedResult<KnowledgeRevisionSummary>> ListRevisionsAsync(
        Guid knowledgeId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
                await KnowledgeStore.ListRevisionsAsync(
                    context,
                    knowledgeId,
                    pageSize,
                    cursor,
                    token),
            cancellationToken).AsTask();

    public Task<KnowledgeContentChunk> ReadContentAsync(
        Guid knowledgeId,
        int? revision,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
                await KnowledgeStore.ReadContentAsync(
                    context,
                    knowledgeId,
                    revision,
                    offset,
                    maxChars,
                    token),
            cancellationToken).AsTask();

    public Task<KnowledgeDocument> ExportDocumentAsync(
        Guid knowledgeId,
        int revision,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
                await KnowledgeStore.ExportDocumentAsync(
                    context,
                    knowledgeId,
                    revision,
                    token),
            cancellationToken).AsTask();

    public Task<PagedResult<KnowledgeRelationItem>> ListRelationsAsync(
        Guid knowledgeId,
        KnowledgeRelationStatus? status,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
                await KnowledgeStore.ListRelationsAsync(
                    context,
                    knowledgeId,
                    status,
                    pageSize,
                    cursor,
                    token),
            cancellationToken).AsTask();

    public Task<LocalKnowledgeGraph> GetLocalGraphAsync(
        Guid centerKnowledgeId,
        int depth,
        int maxNodes,
        int maxEdges,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
                await KnowledgeStore.GetLocalGraphAsync(
                    context,
                    centerKnowledgeId,
                    depth,
                    maxNodes,
                    maxEdges,
                    token),
            cancellationToken).AsTask();
}
