using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class CipherVaultConfigurationTests
{
    [Fact]
    public void MissingDefaultConfigUsesDefaults()
    {
        using var fixture = new ConfigFixture();

        var result = CipherVaultConfiguration.LoadCore(
            CipherVaultConfigOverrides.Empty,
            commandLineConfigPath: null,
            getEnvironmentVariable: _ => null,
            defaultConfigPath: Path.Combine(fixture.Root, "missing.json"),
            defaultVaultPath: Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Equal(
            Path.Combine(fixture.Root, "default-vault"),
            result.VaultPath);
        Assert.Equal(TimeSpan.FromMinutes(15), result.SessionOptions.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(4), result.SessionOptions.HardLifetime);
        Assert.Equal(McpAccessMode.ReadWrite, result.McpAccessMode);
    }

    [Fact]
    public void CommandLineOverridesEnvironmentAndConfig()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            """
            {
              "configVersion": 1,
              "vaultPath": "config-vault",
              "session": {
                "idleMinutes": 20,
                "hardHours": 5
              },
              "mcp": {
                "accessMode": "readOnly"
              }
            }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_PATH"] = Path.Combine(fixture.Root, "env-vault"),
            ["CIPHER_VAULT_IDLE_MINUTES"] = "30",
            ["CIPHER_VAULT_HARD_HOURS"] = "6",
            ["CIPHER_VAULT_ACCESS_MODE"] = "readWrite"
        };

        var result = CipherVaultConfiguration.LoadCore(
            new CipherVaultConfigOverrides
            {
                VaultPath = Path.Combine(fixture.Root, "cli-vault"),
                IdleMinutes = 45,
                HardHours = 8,
                AccessMode = McpAccessMode.ReadOnly
            },
            configPath,
            key => environment.GetValueOrDefault(key),
            Path.Combine(fixture.Root, "default.json"),
            Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.Root, "cli-vault"), result.VaultPath);
        Assert.Equal(TimeSpan.FromMinutes(45), result.SessionOptions.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(8), result.SessionOptions.HardLifetime);
        Assert.Equal(McpAccessMode.ReadOnly, result.McpAccessMode);
        Assert.Equal(configPath, result.ConfigPath);
    }

    [Fact]
    public void PerFieldPrecedenceFallsThroughToConfig()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            """
            {
              "configVersion": 1,
              "session": {
                "idleMinutes": 20,
                "hardHours": 5
              },
              "mcp": {
                "accessMode": "readOnly"
              }
            }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_HARD_HOURS"] = "6"
        };

        var result = CipherVaultConfiguration.LoadCore(
            CipherVaultConfigOverrides.Empty,
            configPath,
            key => environment.GetValueOrDefault(key),
            Path.Combine(fixture.Root, "default.json"),
            Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(20), result.SessionOptions.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(6), result.SessionOptions.HardLifetime);
        Assert.Equal(McpAccessMode.ReadOnly, result.McpAccessMode);
    }

    [Fact]
    public void InvalidEnvironmentValueIsIgnoredWhenCommandLineProvidesField()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig("{ \"configVersion\": 1 }");
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_IDLE_MINUTES"] = "not-an-integer"
        };

        var result = CipherVaultConfiguration.Load(
            new CipherVaultConfigOverrides { IdleMinutes = 25 },
            configPath,
            key => environment.GetValueOrDefault(key));

        Assert.Equal(TimeSpan.FromMinutes(25), result.SessionOptions.IdleTimeout);
    }

    [Fact]
    public void RelativeConfigVaultPathUsesConfigDirectory()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            """
            {
              "configVersion": 1,
              "vaultPath": "nested/vault"
            }
            """);

        var result = CipherVaultConfiguration.LoadCore(
            CipherVaultConfigOverrides.Empty,
            configPath,
            _ => null,
            Path.Combine(fixture.Root, "default.json"),
            Path.Combine(fixture.Root, "default-vault"),
            CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.Root, "nested", "vault"), result.VaultPath);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            """
            {
              "configVersion": 1,
              "futureSetting": true,
              "session": {
                "idleMinutes": 25,
                "futureSessionSetting": "ignored"
              }
            }
            """);

        var result = CipherVaultConfiguration.Load(
            CipherVaultConfigOverrides.Empty,
            configPath,
            _ => null);

        Assert.Equal(TimeSpan.FromMinutes(25), result.SessionOptions.IdleTimeout);
    }

    [Fact]
    public void EnvironmentConfigPathIsUsedWhenCommandLinePathIsMissing()
    {
        using var fixture = new ConfigFixture();
        var environmentConfigPath = fixture.WriteConfig(
            """
            {
              "configVersion": 1,
              "session": {
                "idleMinutes": 35
              }
            }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_CONFIG"] = environmentConfigPath
        };

        var result = CipherVaultConfiguration.Load(
            CipherVaultConfigOverrides.Empty,
            commandLineConfigPath: null,
            key => environment.GetValueOrDefault(key));

        Assert.Equal(environmentConfigPath, result.ConfigPath);
        Assert.Equal(TimeSpan.FromMinutes(35), result.SessionOptions.IdleTimeout);
    }

    [Fact]
    public void CommandLineConfigPathOverridesEnvironmentConfigPath()
    {
        using var fixture = new ConfigFixture();
        var commandLineConfig = Path.Combine(fixture.Root, "command.json");
        var environmentConfig = Path.Combine(fixture.Root, "environment.json");
        File.WriteAllText(
            commandLineConfig,
            """
            {
              "configVersion": 1,
              "session": {
                "idleMinutes": 40
              }
            }
            """);
        File.WriteAllText(
            environmentConfig,
            """
            {
              "configVersion": 1,
              "session": {
                "idleMinutes": 50
              }
            }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_CONFIG"] = environmentConfig
        };

        var result = CipherVaultConfiguration.Load(
            CipherVaultConfigOverrides.Empty,
            commandLineConfigPath: commandLineConfig,
            key => environment.GetValueOrDefault(key));

        Assert.Equal(commandLineConfig, result.ConfigPath);
        Assert.Equal(TimeSpan.FromMinutes(40), result.SessionOptions.IdleTimeout);
    }

    [Fact]
    public void EnvironmentVariablesInVaultPathAreExpanded()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            """
            {
              "configVersion": 1,
              "vaultPath": "%CIPHER_VAULT_TEST_ROOT%/vault"
            }
            """);
        var previous = Environment.GetEnvironmentVariable("CIPHER_VAULT_TEST_ROOT");
        Environment.SetEnvironmentVariable("CIPHER_VAULT_TEST_ROOT", fixture.Root);
        try
        {
            var result = CipherVaultConfiguration.Load(
                CipherVaultConfigOverrides.Empty,
                configPath,
                _ => null);

            Assert.Equal(Path.Combine(fixture.Root, "vault"), result.VaultPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CIPHER_VAULT_TEST_ROOT", previous);
        }
    }

    [Theory]
    [InlineData("\"password\": \"secret\"")]
    [InlineData("\"recovery_key\": \"key\"")]
    [InlineData("\"nested\": {\"access_token\": \"token\"}")]
    public void SensitiveFieldsAreRejected(string field)
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            $$"""
            {
              "configVersion": 1,
              {{field}}
            }
            """);

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultConfiguration.Load(
                CipherVaultConfigOverrides.Empty,
                configPath,
                _ => null));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void ExplicitMissingConfigIsRejected()
    {
        using var fixture = new ConfigFixture();
        var missing = Path.Combine(fixture.Root, "missing.json");

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultConfiguration.Load(
                CipherVaultConfigOverrides.Empty,
                missing,
                _ => null));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void Utf8BomConfigIsAccepted()
    {
        using var fixture = new ConfigFixture();
        var configPath = Path.Combine(fixture.Root, "config-with-bom.json");
        File.WriteAllText(
            configPath,
            """
            {
              "configVersion": 1,
              "session": {
                "idleMinutes": 25
              }
            }
            """,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = CipherVaultConfiguration.Load(
            CipherVaultConfigOverrides.Empty,
            configPath,
            _ => null);

        Assert.Equal(TimeSpan.FromMinutes(25), result.SessionOptions.IdleTimeout);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"configVersion\": 2 }")]
    [InlineData("{ \"configVersion\": 1, \"session\": { \"idleMinutes\": \"15\" } }")]
    [InlineData("{ not-json")]
    public void InvalidConfigIsRejected(string json)
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(json);

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultConfiguration.Load(
                CipherVaultConfigOverrides.Empty,
                configPath,
                _ => null));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void AccessModeConflictIsRejected()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig("{ \"configVersion\": 1 }");
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_ACCESS_MODE"] = "readWrite",
            ["CIPHER_VAULT_READ_ONLY"] = "1"
        };

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultConfiguration.Load(
                CipherVaultConfigOverrides.Empty,
                configPath,
                key => environment.GetValueOrDefault(key)));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void LegacyReadOnlyFalseOverridesConfigToReadWrite()
    {
        using var fixture = new ConfigFixture();
        var configPath = fixture.WriteConfig(
            """
            {
              "configVersion": 1,
              "mcp": {
                "accessMode": "readOnly"
              }
            }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["CIPHER_VAULT_READ_ONLY"] = "0"
        };

        var result = CipherVaultConfiguration.Load(
            CipherVaultConfigOverrides.Empty,
            configPath,
            key => environment.GetValueOrDefault(key));

        Assert.Equal(McpAccessMode.ReadWrite, result.McpAccessMode);
    }

    [Fact]
    public void OversizedConfigIsRejected()
    {
        using var fixture = new ConfigFixture();
        var configPath = Path.Combine(fixture.Root, "large.json");
        File.WriteAllText(configPath, new string(' ', (256 * 1024) + 1));

        var exception = Assert.Throws<VaultException>(() =>
            CipherVaultConfiguration.Load(
                CipherVaultConfigOverrides.Empty,
                configPath,
                _ => null));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private sealed class ConfigFixture : IDisposable
    {
        public ConfigFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-config-{Guid.NewGuid():N}");
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
