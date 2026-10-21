namespace CipherVault.Host.Security;

public interface IAdminTokenStore
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
}
