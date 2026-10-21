using CipherVault.Auth.Win;
using CipherVault.Core.Configuration;

namespace CipherVault.Host.Security;

public sealed class WindowsHostAccessGrantPrompt : IHostAccessGrantPrompt
{
    private readonly Func<
        AccessGrantPromptRequest,
        CancellationToken,
        Task<AccessGrantPromptResult>> _request;

    public WindowsHostAccessGrantPrompt()
        : this((request, cancellationToken) =>
            WindowsAccessGrantPrompt.RequestAsync(
                "CipherVault 访问授权",
                request,
                cancellationToken))
    {
    }

    public WindowsHostAccessGrantPrompt(
        Func<
            AccessGrantPromptRequest,
            CancellationToken,
            Task<AccessGrantPromptResult>> request)
    {
        _request = request;
    }

    public async Task<HostAccessGrantPromptResult> RequestAsync(
        HostAccessGrantPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _request(
            new AccessGrantPromptRequest(
                request.ClientId,
                request.ClientInstanceId,
                request.TokenAccessMode == McpAccessMode.ReadWrite
                    ? "readWrite"
                    : "readOnly",
                AccessGrantScopes.ToNames(request.AllowedScopes),
                AccessGrantScopes.ToNames(request.RequestedScopes),
                FormatDuration(request.IdleLifetime),
                FormatDuration(request.HardLifetime)),
            cancellationToken).ConfigureAwait(false);
        if (!result.Confirmed)
        {
            return new HostAccessGrantPromptResult(
                false,
                AccessGrantScope.None);
        }

        var scopes = AccessGrantScope.None;
        if (result.Read)
        {
            scopes |= AccessGrantScope.Read;
        }
        if (result.Write)
        {
            scopes |= AccessGrantScope.Write;
        }
        if (result.Delete)
        {
            scopes |= AccessGrantScope.Delete;
        }
        return new HostAccessGrantPromptResult(
            true,
            AccessGrantScopes.Normalize(scopes));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.#} 小时";
        }
        return $"{duration.TotalMinutes:0.#} 分钟";
    }
}
