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
        Assert.EndsWith(
            Path.Combine("config", "git-backup-state.json"),
            options.Paths.GitBackupStatePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GitBackupIsDisabledByDefault()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-disabled-{Guid.NewGuid():N}");
        var options = ServerOptions.Load(
            CreateConfiguration(
                parent,
                new Dictionary<string, string?>()));

        Assert.False(options.GitBackup.Enabled);
        Assert.Null(options.GitBackup.ConfigurationErrorCode);
    }

    [Fact]
    public void GitBackupLoadsValidConfiguration()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-valid-{Guid.NewGuid():N}");
        var repository = Path.Combine(parent, "backup-repo");
        Directory.CreateDirectory(repository);
        try
        {
            var options = ServerOptions.Load(
                CreateConfiguration(
                    parent,
                    new Dictionary<string, string?>
                    {
                        ["CIPHERVAULT_GIT_BACKUP_ENABLED"] = "true",
                        ["CIPHERVAULT_GIT_BACKUP_REPO"] = repository,
                        ["CIPHERVAULT_GIT_BACKUP_REMOTE"] = "backup",
                        ["CIPHERVAULT_GIT_BACKUP_BRANCH"] = "vault-main",
                        ["CIPHERVAULT_GIT_BACKUP_DEBOUNCE_SECONDS"] = "120",
                        ["CIPHERVAULT_GIT_BACKUP_MAX_DELAY_SECONDS"] = "600",
                        ["CIPHERVAULT_GIT_BACKUP_RETRY_SECONDS"] = "90",
                        ["CIPHERVAULT_GIT_BACKUP_ROTATE_BYTES"] = "209715200",
                        ["CIPHERVAULT_GIT_BACKUP_ROTATE_DAYS"] = "180"
                    }));

            Assert.True(options.GitBackup.Enabled);
            Assert.Equal(repository, options.GitBackup.RepositoryPath);
            Assert.Equal("backup", options.GitBackup.Remote);
            Assert.Equal("vault-main", options.GitBackup.Branch);
            Assert.Equal(TimeSpan.FromSeconds(120), options.GitBackup.DebounceDelay);
            Assert.Equal(TimeSpan.FromSeconds(600), options.GitBackup.MaxDelay);
            Assert.Equal(TimeSpan.FromSeconds(90), options.GitBackup.RetryDelay);
            Assert.Equal(209715200, options.GitBackup.RotationBytes);
            Assert.Equal(TimeSpan.FromDays(180), options.GitBackup.RotationAge);
            Assert.Null(options.GitBackup.ConfigurationErrorCode);
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
    [InlineData("CIPHERVAULT_GIT_BACKUP_ENABLED", "yes")]
    [InlineData("CIPHERVAULT_GIT_BACKUP_DEBOUNCE_SECONDS", "30")]
    [InlineData("CIPHERVAULT_GIT_BACKUP_MAX_DELAY_SECONDS", "120")]
    [InlineData("CIPHERVAULT_GIT_BACKUP_ROTATE_BYTES", "1024")]
    public void InvalidGitBackupConfigurationFailsClosed(
        string key,
        string value)
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-invalid-{Guid.NewGuid():N}");
        var repository = Path.Combine(parent, "backup-repo");
        Directory.CreateDirectory(repository);
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["CIPHERVAULT_GIT_BACKUP_ENABLED"] = "true",
                ["CIPHERVAULT_GIT_BACKUP_REPO"] = repository,
                [key] = value
            };
            var options = ServerOptions.Load(
                CreateConfiguration(parent, values));

            Assert.True(options.GitBackup.Enabled);
            Assert.Equal(
                GitBackupOptions.InvalidConfigurationCode,
                options.GitBackup.ConfigurationErrorCode);
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
    public void GitBackupRepositoryCannotOverlapVaultPaths()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-git-overlap-{Guid.NewGuid():N}");
        var repository = Path.Combine(parent, "data", "backup-repo");
        Directory.CreateDirectory(repository);
        try
        {
            var options = ServerOptions.Load(
                CreateConfiguration(
                    parent,
                    new Dictionary<string, string?>
                    {
                        ["CIPHERVAULT_GIT_BACKUP_ENABLED"] = "true",
                        ["CIPHERVAULT_GIT_BACKUP_REPO"] = repository
                    }));

            Assert.Equal(
                GitBackupOptions.InvalidConfigurationCode,
                options.GitBackup.ConfigurationErrorCode);
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
