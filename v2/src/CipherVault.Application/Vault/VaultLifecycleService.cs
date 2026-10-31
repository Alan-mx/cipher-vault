using CipherVault.Core.Errors;
using CipherVault.Core.Crypto;

namespace CipherVault.Application.Vault;

public sealed record VaultUnlockResult(
    VaultSessionStatus Status,
    bool CredentialAccepted);

public sealed class VaultLifecycleService
{
    private readonly VaultSessionManager sessionManager;
    private readonly SemaphoreSlim unlockGate = new(1, 1);
    private readonly SemaphoreSlim passwordChangeGate = new(1, 1);

    public VaultLifecycleService(VaultSessionManager sessionManager)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
    }

    public Task<VaultSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.GetStatusAsync(cancellationToken);

    public Task<VaultSessionStatus> SetupAsync(
        string password,
        bool riskAccepted,
        CancellationToken cancellationToken = default)
    {
        if (!riskAccepted)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Password-loss risk acknowledgement is required.");
        }

        return sessionManager.InitializeAsync(password, cancellationToken);
    }

    public async Task<VaultUnlockResult> UnlockAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        await unlockGate.WaitAsync(cancellationToken);
        try
        {
            var initial = await sessionManager.GetStatusAsync(cancellationToken);
            if (initial.State != VaultSessionState.Unlocked)
            {
                var unlocked = await sessionManager.UnlockAsync(
                    password,
                    cancellationToken);
                return new VaultUnlockResult(unlocked, CredentialAccepted: true);
            }

            using var verification = await VaultKeyService.UnlockAsync(
                sessionManager.Paths.KeyPath,
                password,
                cancellationToken);
            var current = await sessionManager.GetStatusAsync(cancellationToken);
            return new VaultUnlockResult(
                current,
                CredentialAccepted: true);
        }
        finally
        {
            unlockGate.Release();
        }
    }

    public Task<VaultSessionStatus> LockAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.LockAsync(cancellationToken);

    public ValueTask TouchActivityAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.TouchActivityAsync(cancellationToken);

    public async Task<VaultSessionStatus> ChangePasswordAsync(
        string oldPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        await passwordChangeGate.WaitAsync(cancellationToken);
        try
        {
            return await sessionManager.ChangePasswordAndLockAsync(
                oldPassword,
                newPassword,
                cancellationToken);
        }
        finally
        {
            passwordChangeGate.Release();
        }
    }
}
