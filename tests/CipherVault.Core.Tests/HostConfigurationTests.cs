using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class HostConfigurationTests
{
    private static readonly string TokenHash = "sha256:" + new string('a', 64);

    [Fact]
    public void ValidHostConfigIsParsed()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            $$"""
            {
              "configVersion": 2,
              "vaultPath": "vault",
              "session": {
                "idleMinutes": 20,
                "hardHours": 5
              },
              "host": {
                "bindAddress": "127.0.0.1",
                "port": 45123,
                "mcpPath": "/mcp",
                "adminPath": "/admin/v1",
                "maxRequestBodyBytes": 2048,
                "allowedOrigins": ["http://localhost:3000"]
              },
              "clients": [
                {
                  "id": "codex-readonly",
                  "tokenHash": "{{TokenHash}}",
                  "accessMode": "readOnly",
                  "allowLock": false,
                  "enabled": true
                }
              ]
            }
            """);

        var result = CipherVaultHostConfiguration.LoadCore(
            CipherVaultHostConfigOverrides.Empty,
            configPath,
            _ => null,
            Path.Combine(fixture.Root, "default.json"),
            Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.Root, "vault"), result.VaultPath);
        Assert.Equal(TimeSpan.FromMinutes(20), result.SessionOptions.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(5), result.SessionOptions.HardLifetime);
        Assert.Equal("127.0.0.1", result.Endpoint.BindAddress);
        Assert.Equal(45123, result.Endpoint.Port);
        Assert.Equal("/mcp", result.Endpoint.McpPath);
        Assert.Equal("/admin/v1", result.Endpoint.AdminPath);
        Assert.Equal(2048, result.Endpoint.MaxRequestBodyBytes);
        Assert.Contains("http://localhost:3000", result.Endpoint.AllowedOrigins);
        var client = Assert.Single(result.Clients);
        Assert.Equal("codex-readonly", client.ClientId);
        Assert.Equal(McpAccessMode.ReadOnly, client.AccessMode);
        Assert.Equal(32, client.TokenHash.Length);
    }

    [Fact]
    public void MissingDefaultHostConfigUsesDefaults()
    {
        using var fixture = new ConfigFixture();

        var result = CipherVaultHostConfiguration.LoadCore(
            CipherVaultHostConfigOverrides.Empty,
            commandLineConfigPath: null,
            getEnvironmentVariable: _ => null,
            defaultConfigPath: Path.Combine(fixture.Root, "missing.json"),
            defaultVaultPath: Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.Root, "default-vault"), result.VaultPath);
        Assert.Equal(TimeSpan.FromMinutes(15), result.SessionOptions.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(4), result.SessionOptions.HardLifetime);
        Assert.Equal(43127, result.Endpoint.Port);
        Assert.Equal(4 * 1024 * 1024, result.Endpoint.MaxRequestBodyBytes);
        Assert.Empty(result.Endpoint.AllowedOrigins);
        Assert.Empty(result.Clients);
    }

    [Fact]
    public void CommandLineOverridesHostConfigVaultAndSession()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            """
            {
              "configVersion": 2,
              "vaultPath": "config-vault",
              "session": {
                "idleMinutes": 20,
                "hardHours": 5
              }
            }
            """);

        var result = CipherVaultHostConfiguration.LoadCore(
            new CipherVaultHostConfigOverrides
            {
                VaultPath = Path.Combine(fixture.Root, "cli-vault"),
                IdleMinutes = 30,
                HardHours = 6
            },
            configPath,
            _ => null,
            Path.Combine(fixture.Root, "default.json"),
            Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.Root, "cli-vault"), result.VaultPath);
        Assert.Equal(TimeSpan.FromMinutes(30), result.SessionOptions.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(6), result.SessionOptions.HardLifetime);
    }

    [Theory]
    [InlineData("{ \"configVersion\": 1 }")]
    [InlineData("{ \"configVersion\": 2, \"token\": \"secret\" }")]
    [InlineData("{ \"configVersion\": 2, \"host\": { \"bindAddress\": \"0.0.0.0\" } }")]
    [InlineData("{ \"configVersion\": 2, \"host\": { \"port\": 70000 } }")]
    [InlineData("{ \"configVersion\": 2, \"host\": { \"mcpPath\": \"/\" } }")]
    [InlineData("{ \"configVersion\": 2, \"host\": { \"mcpPath\": \"/mcp?x=1\" } }")]
    [InlineData("{ \"configVersion\": 2, \"host\": { \"mcpPath\": \"/admin/v1/mcp\" } }")]
    [InlineData("{ \"configVersion\": 2, \"host\": { \"allowedOrigins\": [\"*\"] } }")]
    [InlineData("{ \"configVersion\": 2, \"host\": { \"allowedOrigins\": [\"http://localhost:3000/path\"] } }")]
    public void InvalidHostConfigIsRejected(string json)
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(json);

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultHostConfiguration.LoadCore(
                CipherVaultHostConfigOverrides.Empty,
                configPath,
                _ => null,
                Path.Combine(fixture.Root, "default.json"),
                Path.Combine(fixture.Root, "default-vault"),
                CancellationToken.None));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("sha256:1234")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InvalidTokenHashIsRejected(string tokenHash)
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            $$"""
            {
              "configVersion": 2,
              "clients": [
                {
                  "id": "codex",
                  "tokenHash": "{{tokenHash}}",
                  "accessMode": "readOnly"
                }
              ]
            }
            """);

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultHostConfiguration.LoadCore(
                CipherVaultHostConfigOverrides.Empty,
                configPath,
                _ => null,
                Path.Combine(fixture.Root, "default.json"),
                Path.Combine(fixture.Root, "default-vault"),
                CancellationToken.None));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void DuplicateClientIdsAreRejected()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            $$"""
            {
              "configVersion": 2,
              "clients": [
                {
                  "id": "Codex",
                  "tokenHash": "{{TokenHash}}",
                  "accessMode": "readOnly"
                },
                {
                  "id": "codex",
                  "tokenHash": "{{TokenHash}}",
                  "accessMode": "readWrite"
                }
              ]
            }
            """);

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultHostConfiguration.LoadCore(
                CipherVaultHostConfigOverrides.Empty,
                configPath,
                _ => null,
                Path.Combine(fixture.Root, "default.json"),
                Path.Combine(fixture.Root, "default-vault"),
                CancellationToken.None));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void LegacyMcpAccessModeEnvironmentDoesNotAffectHostConfig()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig("{ \"configVersion\": 2 }");
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_ACCESS_MODE"] = "not-valid",
            ["CIPHER_VAULT_READ_ONLY"] = "maybe"
        };

        var result = CipherVaultHostConfiguration.LoadCore(
            CipherVaultHostConfigOverrides.Empty,
            configPath,
            key => environment.GetValueOrDefault(key),
            Path.Combine(fixture.Root, "default.json"),
            Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Empty(result.Clients);
    }

    [Fact]
    public void ExplicitMissingHostConfigIsRejected()
    {
        using var fixture = new ConfigFixture();

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultHostConfiguration.LoadCore(
                CipherVaultHostConfigOverrides.Empty,
                Path.Combine(fixture.Root, "missing.json"),
                _ => null,
                Path.Combine(fixture.Root, "default.json"),
                Path.Combine(fixture.Root, "default-vault"),
                CancellationToken.None));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private sealed class ConfigFixture : IDisposable
    {
        public ConfigFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-host-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string WriteConfig(string json)
        {
            var path = Path.Combine(Root, "config.json");
            File.WriteAllText(path, json);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
