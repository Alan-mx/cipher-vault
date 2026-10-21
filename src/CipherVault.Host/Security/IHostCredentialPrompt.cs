using CipherVault.Core.Session;

namespace CipherVault.Host.Security;

public enum HostCredentialPreference
{
    Any,
    Password,
    RecoveryKey
}

public sealed record HostCredentialPromptResult(
    bool Confirmed,
    string? Credential,
    VaultCredentialType CredentialType);

public interface IHostCredentialPrompt
{
    Task<HostCredentialPromptResult> RequestAsync(
        HostCredentialPreference preference,
        string? message = null,
        CancellationToken cancellationToken = default);
}
