using CipherVault.Core.Configuration;

namespace CipherVault.Host.Security;

public sealed record HostAccessGrantPromptRequest(
    string ClientId,
    Guid ClientInstanceId,
    McpAccessMode TokenAccessMode,
    AccessGrantScope RequestedScopes,
    AccessGrantScope AllowedScopes,
    TimeSpan IdleLifetime,
    TimeSpan HardLifetime);

public sealed record HostAccessGrantPromptResult(
    bool Approved,
    AccessGrantScope ApprovedScopes);

public interface IHostAccessGrantPrompt
{
    Task<HostAccessGrantPromptResult> RequestAsync(
        HostAccessGrantPromptRequest request,
        CancellationToken cancellationToken = default);
}
