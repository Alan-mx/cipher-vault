using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Session;

public sealed class HostSessionCoordinator
{
    private readonly IVaultService _vault;
    private readonly ResolvedCipherVaultHostConfig _configuration;
    private readonly IHostCredentialPrompt _credentialPrompt;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public HostSessionCoordinator(
        IVaultService vault,
        ResolvedCipherVaultHostConfig configuration,
        IHostCredentialPrompt credentialPrompt)
    {
        _vault = vault;
        _configuration = configuration;
        _credentialPrompt = credentialPrompt;
    }

    public async Task<HostSessionSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        return Map(await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<HostSessionSnapshot> UnlockAsync(
        HostCredentialPreference preference,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _vault.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (current.State is VaultSessionState.Unlocked or VaultSessionState.Expiring)
            {
                return Map(current);
            }

            var prompt = await _credentialPrompt.RequestAsync(
                preference,
                cancellationToken).ConfigureAwait(false);
            if (!prompt.Confirmed || string.IsNullOrEmpty(prompt.Credential))
            {
                throw new HostException(
                    HostErrorCode.AccessDenied,
                    "Vault authorization was cancelled.");
            }

            try
            {
                var unlocked = await _vault.UnlockAsync(
                    _configuration.VaultPath,
                    prompt.Credential,
                    prompt.CredentialType,
                    cancellationToken).ConfigureAwait(false);
                return Map(unlocked);
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.InvalidCredential)
            {
                throw new HostException(
                    HostErrorCode.InvalidCredential,
                    "Vault credential is invalid.",
                    exception);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<HostSessionSnapshot> LockAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await _vault.LockAsync(reason, cancellationToken)
                .ConfigureAwait(false);
            return Map(status);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private HostSessionSnapshot Map(VaultSessionStatus status)
    {
        return new HostSessionSnapshot(
            status.State,
            status.SessionId,
            status.StartedAt,
            status.LastUserActivityAt,
            status.IdleExpiresAt,
            status.HardExpiresAt,
            status.LockReason,
            ActiveLeases: 0,
            ConnectedClients: 0);
    }
}
