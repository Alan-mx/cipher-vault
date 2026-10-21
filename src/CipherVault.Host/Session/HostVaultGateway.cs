using CipherVault.Core;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Session;

public sealed class HostVaultGateway
{
    private readonly HostSessionCoordinator _sessions;
    private readonly IVaultService _vault;

    public HostVaultGateway(
        HostSessionCoordinator sessions,
        IVaultService vault)
    {
        _sessions = sessions;
        _vault = vault;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<IVaultService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            expectedSessionId: null,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> ExecuteAsync<T>(
        Guid? expectedSessionId,
        Func<IVaultService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _sessions.AcquireLeaseAsync(cancellationToken)
            .ConfigureAwait(false);
        if (expectedSessionId is not null &&
            lease.SessionId != expectedSessionId)
        {
            throw new HostException(
                HostErrorCode.AccessGrantRevoked,
                "The Access Grant no longer belongs to the active Vault session.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease.CancellationToken);
        return await operation(_vault, linked.Token).ConfigureAwait(false);
    }

    public Task<T> ExecuteMaintenanceAsync<T>(
        Func<IVaultService, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        return _sessions.ExecuteMaintenanceAsync(operation, cancellationToken);
    }
}
