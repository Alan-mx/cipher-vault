using CipherVault.Core.Backup;
using CipherVault.Server.Hosting;
using Microsoft.Extensions.Configuration;

namespace CipherVault.Server.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void ExplicitDirectoriesDoNotRequireUserHome()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-explicit-paths-{Guid.NewGuid():N}");
        var configuration = CreateConfiguration(
            parent,
            new Dictionary<string, string?>());
        var defaultsRequested = false;

        var paths = ServerOptions.ResolvePaths(
            configuration,
            () =>
            {
                defaultsRequested = true;
                throw new InvalidOperationException();
            });

        Assert.False(defaultsRequested);
        Assert.Equal(Path.Combine(parent, "data"), paths.VaultDirectory);
        Assert.Equal(Path.Combine(parent, "config"), paths.ConfigDirectory);
    }

    [Fact]
    public void ConfigDirectoryCannotBeInsideVaultDirectory()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-paths-{Guid.NewGuid():N}");
        try
        {
            var exception = Assert.Throws<ServerStartupException>(
                () => ApplicationPaths.Create(
                    parent,
                    Path.Combine(parent, "config")));
            Assert.Equal(ServerStartupCode.InvalidConfiguration, exception.Code);
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public void BackupDirectoryDefaultsToVaultSibling()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-backup-default-{Guid.NewGuid():N}");
        var configuration = CreateConfiguration(
            parent,
            new Dictionary<string, string?>());

        var options = ServerOptions.Load(configuration);

        Assert.Equal(
            Path.Combine(parent, "backups"),
            options.Paths.BackupDirectory);
        Assert.Equal(
            Path.Combine(parent, "backups", BackupConstants.BackupLockFileName),
            options.Paths.BackupLockPath);
    }

    [Fact]
    public void CustomBackupDirectoryMustBeAbsoluteAndDisjoint()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-backup-custom-{Guid.NewGuid():N}");
        var absolute = Path.Combine(parent, "backups");
        var options = ServerOptions.Load(
            CreateConfiguration(
                parent,
                new Dictionary<string, string?>
                {
                    ["CIPHERVAULT_BACKUP_DIR"] = absolute
                }));
        Assert.Equal(absolute, options.Paths.BackupDirectory);

        var relative = Assert.Throws<ServerStartupException>(
            () => ServerOptions.Load(
                CreateConfiguration(
                    parent,
                    new Dictionary<string, string?>
                    {
                        ["CIPHERVAULT_BACKUP_DIR"] = "relative/backups"
                    })));
        Assert.Equal(
            ServerStartupCode.InvalidConfiguration,
            relative.Code);

        var insideVault = Assert.Throws<ServerStartupException>(
            () => ServerOptions.Load(
                CreateConfiguration(
                    parent,
                    new Dictionary<string, string?>
                    {
                        ["CIPHERVAULT_BACKUP_DIR"] =
                            Path.Combine(parent, "data", "backups")
                    })));
        Assert.Equal(
            ServerStartupCode.InvalidConfiguration,
            insideVault.Code);
    }

    [Fact]
    public void EnsureCreatedCreatesProtectedBackupDirectory()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-backup-create-{Guid.NewGuid():N}");
        try
        {
            var options = ServerOptions.Load(
                CreateConfiguration(
                    parent,
                    new Dictionary<string, string?>()));

            options.Paths.EnsureCreated();

            Assert.True(Directory.Exists(options.Paths.BackupDirectory));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("http://0.0.0.0:43127")]
    [InlineData("https://127.0.0.1:43127")]
    [InlineData("http://example.com:43127")]
    public void ListenAddressMustBeHttpLoopback(string url)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-listen-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_DATA_DIR"] = Path.Combine(parent, "data"),
                ["CIPHERVAULT_CONFIG_DIR"] = Path.Combine(parent, "config"),
                ["ASPNETCORE_URLS"] = url
            })
            .Build();

        var exception = Assert.Throws<ServerStartupException>(
            () => ServerOptions.Load(configuration));
        Assert.Equal(ServerStartupCode.InvalidConfiguration, exception.Code);
    }

    [Fact]
    public void IdleTimeoutMustBeWithinRange()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-idle-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_DATA_DIR"] = Path.Combine(parent, "data"),
                ["CIPHERVAULT_CONFIG_DIR"] = Path.Combine(parent, "config"),
                ["CIPHERVAULT_IDLE_TIMEOUT_MINUTES"] = "121"
            })
            .Build();

        var exception = Assert.Throws<ServerStartupException>(
            () => ServerOptions.Load(configuration));
        Assert.Equal(ServerStartupCode.InvalidConfiguration, exception.Code);
    }

    [Fact]
    public void ContainerModeAllowsWildcardContainerBind()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-container-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_DATA_DIR"] = Path.Combine(parent, "data"),
                ["CIPHERVAULT_CONFIG_DIR"] = Path.Combine(parent, "config"),
                ["CIPHERVAULT_CONTAINER_MODE"] = "true",
                ["CIPHERVAULT_ALLOWED_HOSTS"] = "127.0.0.1,localhost",
                ["ASPNETCORE_URLS"] = "http://0.0.0.0:43127"
            })
            .Build();

        var options = ServerOptions.Load(configuration);

        Assert.True(options.ContainerMode);
        Assert.Equal("0.0.0.0", options.ListenAddresses[0].Host);
        Assert.Equal(["127.0.0.1", "localhost"], options.AllowedHosts);
        Assert.EndsWith(
            Path.Combine("config", "mcp-token.json"),
            options.Paths.McpTokenPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NonLoopbackAllowedHostIsRejected()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-host-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_DATA_DIR"] = Path.Combine(parent, "data"),
                ["CIPHERVAULT_CONFIG_DIR"] = Path.Combine(parent, "config"),
                ["CIPHERVAULT_ALLOWED_HOSTS"] = "example.com"
            })
            .Build();

        var exception = Assert.Throws<ServerStartupException>(
            () => ServerOptions.Load(configuration));
        Assert.Equal(ServerStartupCode.InvalidConfiguration, exception.Code);
    }

    [Fact]
    public void DevelopmentOriginsRequireDevelopmentAndExactLoopbackPort()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-dev-origin-{Guid.NewGuid():N}");
        var validConfiguration = CreateConfiguration(
            parent,
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["CIPHERVAULT_DEVELOPMENT_ORIGINS"] =
                    "http://127.0.0.1:5173,http://localhost:5173"
            });

        var options = ServerOptions.Load(validConfiguration);
        Assert.Equal(
            ["http://127.0.0.1:5173", "http://localhost:5173"],
            options.DevelopmentOrigins
                .Select(origin => origin.GetLeftPart(UriPartial.Authority)));

        var production = CreateConfiguration(
            parent,
            new Dictionary<string, string?>
            {
                ["CIPHERVAULT_DEVELOPMENT_ORIGINS"] =
                    "http://127.0.0.1:5173"
            });
        var exception = Assert.Throws<ServerStartupException>(
            () => ServerOptions.Load(production));
        Assert.Equal(ServerStartupCode.InvalidConfiguration, exception.Code);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5173")]
    [InlineData("https://127.0.0.1:5173")]
    [InlineData("http://example.com:5173")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:5173/path")]
    [InlineData("http://127.0.0.1:5173,http://127.0.0.1:5174/x")]
    public void InvalidDevelopmentOriginsFailClosed(string origins)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-dev-origin-invalid-{Guid.NewGuid():N}");
        var configuration = CreateConfiguration(
            parent,
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["CIPHERVAULT_DEVELOPMENT_ORIGINS"] = origins
            });

        var exception = Assert.Throws<ServerStartupException>(
            () => ServerOptions.Load(configuration));
        Assert.Equal(ServerStartupCode.InvalidConfiguration, exception.Code);
    }

    private static IConfiguration CreateConfiguration(
        string parent,
        IReadOnlyDictionary<string, string?> values)
    {
        var settings = new Dictionary<string, string?>
        {
            ["CIPHERVAULT_DATA_DIR"] = Path.Combine(parent, "data"),
            ["CIPHERVAULT_CONFIG_DIR"] = Path.Combine(parent, "config")
        };
        foreach (var pair in values)
        {
            settings[pair.Key] = pair.Value;
        }
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }
}
