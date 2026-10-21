using CipherVault.Host.Contracts;

namespace CipherVault.Host.Client.Security;

public interface IAdminTokenStore
{
    Task<string> GetTokenAsync(
        HostAdminClientKind clientKind,
        CancellationToken cancellationToken = default);

    Task<HostAdminClientKind?> AuthenticateAsync(
        string rawToken,
        CancellationToken cancellationToken = default);
}
