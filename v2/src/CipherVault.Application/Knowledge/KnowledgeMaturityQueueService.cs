using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Knowledge;
using CipherVault.Core.Storage.Knowledge;

namespace CipherVault.Application.Knowledge;

public sealed class KnowledgeMaturityQueueService
{
    private readonly VaultSessionManager sessionManager;

    public KnowledgeMaturityQueueService(VaultSessionManager sessionManager)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
    }

    public Task<KnowledgeMaturityQueuePage> ListAsync(
        KnowledgeMaturityQueueBucket bucket,
        CursorPageRequest page,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<KnowledgeMaturityQueuePage>(
                KnowledgeMaturityQueueStore.ListAsync(
                    context,
                    new KnowledgeMaturityQueueQuery(bucket, page),
                    token)),
            cancellationToken).AsTask();
}
