using CipherVault.Core.Grants;

namespace CipherVault.Core.Session;

public sealed record VaultSessionClosingContext(
    Guid VaultSessionId,
    string Reason,
    DateTimeOffset OccurredAt);

public interface IVaultSessionClosingHandler
{
    Task HandleClosingAsync(
        VaultSessionClosingContext context,
        IAccessGrantStore store,
        CancellationToken cancellationToken);
}
