using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Mcp.Tests;

public sealed class BridgeSettingsTests
{
    [Fact]
    public void McpUrlOverrideIsRejected()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-bridge-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var tokenEnvironmentName =
            $"CIPHER_VAULT_TEST_TOKEN_{Guid.NewGuid():N}";
        try
        {
            var configPath = Path.Combine(root, "config.json");
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 2,
                    vaultPath = Path.Combine(root, "vault")
                }));
            Environment.SetEnvironmentVariable(
                tokenEnvironmentName,
                "test-agent-token");

            var exception = Assert.Throws<VaultException>(() =>
                BridgeSettings.Load(
                [
                    "--config",
                    configPath,
                    "--token-env",
                    tokenEnvironmentName,
                    "--mcp-url",
                    "https://attacker.example/mcp"
                ]));

            Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
            Assert.Contains(
                "--mcp-url is not supported",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenEnvironmentName, null);
            Directory.Delete(root, recursive: true);
        }
    }
}
