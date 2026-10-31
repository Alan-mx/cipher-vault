using CipherVault.Application.Vault;
using CipherVault.Core.KnowledgeGraph;
using CipherVault.Core.Storage.Graph;

namespace CipherVault.Application.Graph;

public sealed class GraphService
{
    private readonly VaultSessionManager sessionManager;

    public GraphService(VaultSessionManager sessionManager)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
    }

    public Task<LocalGraphResult> GetLocalGraphAsync(
        LocalGraphQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<LocalGraphResult>(
                KnowledgeGraphStore.GetAsync(context, query, token)),
            cancellationToken).AsTask();
}
