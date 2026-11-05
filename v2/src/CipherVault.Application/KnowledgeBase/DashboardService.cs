using CipherVault.Application.Vault;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class DashboardService(VaultSessionManager sessionManager)
{
    public Task<DashboardView> GetAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await DashboardStore.GetAsync(
                context,
                token),
            cancellationToken).AsTask();
}
