using CipherVault.Auth.Win;
using CipherVault.Core.Session;

namespace CipherVault.Host.Security;

public sealed class WindowsHostCredentialPrompt : IHostCredentialPrompt
{
    public async Task<HostCredentialPromptResult> RequestAsync(
        HostCredentialPreference preference,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var preferredType = preference switch
        {
            HostCredentialPreference.Password => VaultCredentialType.Password,
            HostCredentialPreference.RecoveryKey => VaultCredentialType.RecoveryKey,
            _ => (VaultCredentialType?)null
        };
        var result = await WindowsVaultCredentialPrompt.RequestAsync(
            "CipherVault 授权",
            message ?? (preference == HostCredentialPreference.RecoveryKey
                ? "请输入离线恢复密钥。"
                : "AI 或管理端请求访问 CipherVault。"),
            preferredType,
            cancellationToken).ConfigureAwait(false);
        return new HostCredentialPromptResult(
            result.Confirmed,
            result.Credential,
            result.CredentialType);
    }
}
