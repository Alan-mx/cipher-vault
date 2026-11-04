using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Knowledge;
using CipherVault.Core.Storage.Knowledge;

namespace CipherVault.Application.Knowledge;

public sealed class KnowledgeViewService
{
    private readonly VaultSessionManager sessionManager;

    public KnowledgeViewService(VaultSessionManager sessionManager)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
    }

    public Task<KnowledgeFacetPage> GetFacetsAsync(
        KnowledgeViewField field,
        bool activeOnly,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<KnowledgeFacetPage>(
                KnowledgeViewStore.GetFacetsAsync(
                    context,
                    field,
                    activeOnly,
                    token)),
            cancellationToken).AsTask();

    public Task<KnowledgeViewPage> GetDocumentsAsync(
        KnowledgeViewField field,
        string? facetKey,
        bool unset,
        bool activeOnly,
        CursorPageRequest page,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<KnowledgeViewPage>(
                KnowledgeViewStore.GetDocumentsAsync(
                    context,
                    field,
                    facetKey,
                    unset,
                    activeOnly,
                    page,
                    token)),
            cancellationToken).AsTask();
}
