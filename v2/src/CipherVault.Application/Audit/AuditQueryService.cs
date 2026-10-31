using CipherVault.Application.Vault;
using CipherVault.Core.Audit;

namespace CipherVault.Application.Audit;

public sealed class AuditQueryService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;

    public AuditQueryService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AuditPage> ListAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return sessionManager.ExecuteAsync(
            (context, token) => new ValueTask<AuditPage>(
                AuditStore.QueryAsync(
                    context,
                    query,
                    timeProvider.GetUtcNow(),
                    token)),
            cancellationToken).AsTask();
    }
}
