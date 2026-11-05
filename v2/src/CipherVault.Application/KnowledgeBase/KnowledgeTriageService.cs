using CipherVault.Application.Vault;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class KnowledgeTriageService(
    VaultSessionManager sessionManager,
    TimeProvider timeProvider)
{
    public Task<KnowledgeTriagePage> GetAsync(
        KnowledgeTriageQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var model = await KnowledgeModelStore.GetAsync(
                    context,
                    token);
                return await KnowledgeTriageStore.QueryAsync(
                    context,
                    model,
                    query,
                    timeProvider.GetUtcNow(),
                    token);
            },
            cancellationToken).AsTask();
}
