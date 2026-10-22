using System.IO;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CipherVault.Host.Tests;

public sealed class StrictServiceHostTests
{
    private const string Sha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task StrictServiceHostStartsAndStopsThroughHostLifetime()
    {
        using var fixture = new ServiceHostFixture();
        await using var service = StrictServiceHost.Create(
            CreateConfiguration(fixture.Paths),
            fixture.Paths);
        var lifetime = service.Services.GetRequiredService<IHostApplicationLifetime>();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(
            () => started.TrySetResult());

        await service.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task StrictServiceHostHoldsMachineLocksUntilDisposed()
    {
        using var fixture = new ServiceHostFixture();
        var configuration = CreateConfiguration(fixture.Paths);
        var first = StrictServiceHost.Create(configuration, fixture.Paths);

        var exception = Assert.Throws<VaultException>(
            () => StrictServiceHost.Create(configuration, fixture.Paths));

        Assert.Equal(VaultErrorCode.Conflict, exception.Code);

        await first.DisposeAsync();
        await using var second = StrictServiceHost.Create(
            configuration,
            fixture.Paths);
    }

    [Theory]
    [InlineData("compatibility")]
    [InlineData("mismatched-path")]
    public void InvalidServiceHostConfigurationIsRejected(string scenario)
    {
        using var fixture = new ServiceHostFixture();
        var configuration = CreateConfiguration(fixture.Paths);
        if (scenario == "compatibility")
        {
            configuration = configuration with
            {
                SecurityMode = CipherVaultServiceConfiguration.CompatibilityModeValue
            };
        }
        else
        {
            configuration = configuration with
            {
                DataRoot = Path.Combine(fixture.Root, "other")
            };
        }

        var exception = Assert.Throws<VaultException>(
            () => StrictServiceHost.Create(configuration, fixture.Paths));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private static CipherVaultServiceConfigFile CreateConfiguration(
        CipherVaultServicePaths paths)
    {
        return new CipherVaultServiceConfigFile
        {
            ConfigVersion = CipherVaultServiceConfiguration.CurrentConfigVersion,
            SecurityMode = CipherVaultServiceConfiguration.StrictModeValue,
            OwnerSid = "S-1-5-21-1",
            ServiceAccount = @"NT SERVICE\CipherVaultHost",
            VaultPath = paths.VaultPath,
            DataRoot = paths.DataRoot,
            Session = new CipherVaultSessionConfigFile
            {
                IdleMinutes = 15,
                HardHours = 4
            },
            Host = new CipherVaultServiceHostConfigFile
            {
                McpPath = "/mcp",
                MaxRequestBodyBytes = 4 * 1024 * 1024,
                AllowedOrigins = []
            },
            Pipes = new CipherVaultPipeConfigFile
            {
                Admin = "CipherVault.Admin.v1",
                Broker = "CipherVault.Broker.v1",
                Bridge = "CipherVault.Bridge.v1",
                RejectRemoteClients = true
            },
            ProcessPolicy = new CipherVaultProcessPolicyConfigFile
            {
                AllowedSignerThumbprints = [Sha256],
                RequireSha256 = true
            },
            Agent = new CipherVaultAgentRuntimeConfigFile
            {
                StrictTransport = "stdio-bridge",
                CompatibilityHttpEnabled = false
            }
        };
    }

    private sealed class ServiceHostFixture : IDisposable
    {
        public ServiceHostFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-service-host-{Guid.NewGuid():N}");
            Paths = CipherVaultServicePaths.Create(
                Path.Combine(Root, "ProgramFiles"),
                Path.Combine(Root, "ProgramData"),
                Path.Combine(Root, "LocalAppData"));
        }

        public string Root { get; }

        public CipherVaultServicePaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
