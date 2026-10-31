using CipherVault.Server.Hosting;
using Microsoft.Extensions.Configuration;

namespace CipherVault.Server.Tests;

public sealed class ServerOptionsTests
{
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
}
