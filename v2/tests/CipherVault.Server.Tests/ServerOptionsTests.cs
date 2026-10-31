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
}
