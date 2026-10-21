using CipherVault.Auth.Win;
using CipherVault.Core.Configuration;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class WindowsHostAccessGrantPromptTests
{
    [Fact]
    public async Task ApprovedSelectionMapsToNormalizedScopes()
    {
        AccessGrantPromptRequest? captured = null;
        var prompt = new WindowsHostAccessGrantPrompt(
            (request, _) =>
            {
                captured = request;
                return Task.FromResult(
                    new AccessGrantPromptResult(
                        Confirmed: true,
                        Read: true,
                        Write: true,
                        Delete: false));
            });

        var result = await prompt.RequestAsync(new HostAccessGrantPromptRequest(
            "agent",
            Guid.NewGuid(),
            McpAccessMode.ReadWrite,
            AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete,
            AccessGrantScope.Read | AccessGrantScope.Write | AccessGrantScope.Delete,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(4)));

        Assert.True(result.Approved);
        Assert.Equal(
            AccessGrantScope.Read | AccessGrantScope.Write,
            result.ApprovedScopes);
        Assert.NotNull(captured);
        Assert.Contains("delete", captured.AllowedScopes);
        Assert.Contains("delete", captured.RequestedScopes);
    }

    [Fact]
    public async Task CancelledSelectionReturnsNoScopes()
    {
        var prompt = new WindowsHostAccessGrantPrompt(
            (_, _) => Task.FromResult(
                new AccessGrantPromptResult(
                    Confirmed: false,
                    Read: true,
                    Write: true,
                    Delete: true)));

        var result = await prompt.RequestAsync(new HostAccessGrantPromptRequest(
            "agent",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            AccessGrantScope.Read,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(4)));

        Assert.False(result.Approved);
        Assert.Equal(AccessGrantScope.None, result.ApprovedScopes);
    }
}
