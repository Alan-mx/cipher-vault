using CipherVault.Application.Vault;
using CipherVault.Core.Search;
using CipherVault.Core.Storage.Search;

namespace CipherVault.Application.Search;

public sealed class SearchService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;

    public SearchService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SearchPage> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<SearchPage>(
                SearchStore.SearchAsync(
                    context,
                    query,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();
}
