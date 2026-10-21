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

    [Theory]
    [InlineData(new string[0], new[] { "read" }, true)]
    [InlineData(new[] { "--access-mode", "readWrite" }, new[] { "read", "write" }, false)]
    [InlineData(
        new[] { "--access-mode", "readWrite", "--allow-delete" },
        new[] { "read", "write", "delete" },
        false)]
    [InlineData(
        new[] { "--scopes", "read,write,delete" },
        new[] { "read", "write", "delete" },
        false)]
    public void RequestedScopesFollowBridgeArguments(
        string[] arguments,
        string[] expectedScopes,
        bool expectedReadOnly)
    {
        using var fixture = new BridgeFixture();
        var actual = BridgeSettings.Load(
            [
                "--config",
                fixture.ConfigPath,
                "--token-env",
                fixture.TokenEnvironmentName,
                .. arguments
            ]);

        Assert.Equal(
            expectedScopes.OrderBy(value => value, StringComparer.Ordinal),
            actual.RequestedScopes.OrderBy(
                value => value,
                StringComparer.Ordinal));
        Assert.Equal(expectedReadOnly, actual.ReadOnly);
    }

    [Fact]
    public void ReadOnlyConflictsWithRequestedWriteScopes()
    {
        using var fixture = new BridgeFixture();
        var exception = Assert.Throws<VaultException>(() =>
            BridgeSettings.Load(
            [
                "--config",
                fixture.ConfigPath,
                "--token-env",
                fixture.TokenEnvironmentName,
                "--read-only",
                "--scopes",
                "read,write"
            ]));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void EmptyScopesAreRejected()
    {
        using var fixture = new BridgeFixture();
        var exception = Assert.Throws<VaultException>(() =>
            BridgeSettings.Load(
            [
                "--config",
                fixture.ConfigPath,
                "--token-env",
                fixture.TokenEnvironmentName,
                "--scopes",
                ""
            ]));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private sealed class BridgeFixture : IDisposable
    {
        public BridgeFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-bridge-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            ConfigPath = Path.Combine(Root, "config.json");
            TokenEnvironmentName = $"CIPHER_VAULT_TEST_TOKEN_{Guid.NewGuid():N}";
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 2,
                    vaultPath = Path.Combine(Root, "vault")
                }));
            Environment.SetEnvironmentVariable(
                TokenEnvironmentName,
                "test-agent-token");
        }

        public string Root { get; }
        public string ConfigPath { get; }
        public string TokenEnvironmentName { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(TokenEnvironmentName, null);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
