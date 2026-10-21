using CipherVault.Core;

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
        await using var lease = await _sessions.AcquireLeaseAsync(cancellationToken)
            .ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease.CancellationToken);
        return await operation(_vault, linked.Token).ConfigureAwait(false);
    }
}
