using CipherVault.Application.Vault;
using CipherVault.Core.RelatedKnowledge;
using CipherVault.Core.Storage.RelatedKnowledge;

namespace CipherVault.Application.RelatedKnowledge;

public sealed class RelatedKnowledgeService
{
    private readonly VaultSessionManager sessionManager;

    public RelatedKnowledgeService(VaultSessionManager sessionManager)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
    }

    public Task<IReadOnlyList<RelatedDocumentItem>> GetAsync(
        Guid documentId,
        int limit,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<IReadOnlyList<RelatedDocumentItem>>(
                RelatedKnowledgeStore.GetAsync(
                    context,
                    documentId,
                    limit,
                    token)),
            cancellationToken).AsTask();
}
