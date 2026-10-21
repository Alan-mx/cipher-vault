using System.IO;
using CipherVault.Host.Client.Security;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class DpapiAdminTokenStoreTests
{
    [Fact]
    public async Task UiAndCliTokensRoundTripAndAreNotPlaintext()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-admin-token-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = new DpapiAdminTokenStore(root);
            var uiToken = await first.GetTokenAsync(HostAdminClientKind.Ui);
            var cliToken = await first.GetTokenAsync(HostAdminClientKind.Cli);

            Assert.NotEqual(uiToken, cliToken);
            var uiPath = Path.Combine(root, "admin-ui-token.bin");
            var cliPath = Path.Combine(root, "admin-cli-token.bin");
            Assert.True(File.Exists(uiPath));
            Assert.True(File.Exists(cliPath));
            Assert.DoesNotContain(
                uiToken,
                System.Text.Encoding.UTF8.GetString(
                    await File.ReadAllBytesAsync(uiPath)),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                cliToken,
                System.Text.Encoding.UTF8.GetString(
                    await File.ReadAllBytesAsync(cliPath)),
                StringComparison.Ordinal);

            var second = new DpapiAdminTokenStore(root);
            Assert.Equal(
                uiToken,
                await second.GetTokenAsync(HostAdminClientKind.Ui));
            Assert.Equal(
                cliToken,
                await second.GetTokenAsync(HostAdminClientKind.Cli));
            Assert.Equal(
                HostAdminClientKind.Ui,
                await second.AuthenticateAsync(uiToken));
            Assert.Equal(
                HostAdminClientKind.Cli,
                await second.AuthenticateAsync(cliToken));
            Assert.Null(await second.AuthenticateAsync("invalid-token"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
