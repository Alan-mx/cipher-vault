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
        var configuration = CreateConfiguration(fixture.Paths);
        CreateInstallState(fixture.Paths, configuration);
        await using var service = StrictServiceHost.Create(
            configuration,
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
        CreateInstallState(fixture.Paths, configuration);
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
        CreateInstallState(fixture.Paths, configuration);
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

    [Fact]
    public void StrictServiceHostRejectsMissingVaultModeMarker()
    {
        using var fixture = new ServiceHostFixture();
        var configuration = CreateConfiguration(fixture.Paths);
        CreateInstallManifest(fixture.Paths, Guid.NewGuid());

        var exception = Assert.Throws<VaultException>(
            () => StrictServiceHost.Create(configuration, fixture.Paths));

        Assert.Equal(VaultErrorCode.NotFound, exception.Code);
    }

    [Fact]
    public void StrictServiceHostRejectsInstallIdMismatch()
    {
        using var fixture = new ServiceHostFixture();
        var configuration = CreateConfiguration(fixture.Paths);
        CreateInstallState(fixture.Paths, configuration);
        var mode = VaultModeStore.Read(fixture.Paths.ModePath);
        VaultModeStore.Write(
            fixture.Paths.ModePath,
            mode with { InstallId = Guid.NewGuid() });

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

    private static void CreateInstallState(
        CipherVaultServicePaths paths,
        CipherVaultServiceConfigFile configuration)
    {
        var installId = Guid.NewGuid();
        CreateInstallManifest(paths, installId);
        VaultModeStore.Write(
            paths.ModePath,
            new CipherVaultVaultModeManifest
            {
                Format = CipherVaultServiceConfiguration.VaultModeFormat,
                FormatVersion =
                    CipherVaultServiceConfiguration.VaultModeFormatVersion,
                Mode = CipherVaultServiceConfiguration.StrictModeValue,
                OwnerSid = configuration.OwnerSid,
                InstallId = installId,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    private static void CreateInstallManifest(
        CipherVaultServicePaths paths,
        Guid installId)
    {
        Directory.CreateDirectory(paths.DataRoot);
        var imagePath = Path.Combine(
            paths.InstallRoot,
            "CipherVault.Host.exe").Replace(
            "\\",
            "\\\\",
            StringComparison.Ordinal);
        File.WriteAllText(
            paths.PublicManifestPath,
            $$"""
            {
              "format": "cipher-vault-install",
              "formatVersion": 1,
              "installId": "{{installId:D}}",
              "productVersion": "1.0.0",
              "serviceName": "CipherVaultHost",
              "serviceAccount": "NT SERVICE\\CipherVaultHost",
              "serviceSid": "S-1-5-80-1",
              "ownerSid": "S-1-5-21-1",
              "pipes": { "admin": "CipherVault.Admin.v1", "broker": "CipherVault.Broker.v1", "bridge": "CipherVault.Bridge.v1", "rejectRemoteClients": true },
              "allowedServerImages": [
                {
                  "kind": "host",
                  "path": "{{imagePath}}",
                  "sha256": "{{Sha256}}",
                  "signerThumbprint": "{{Sha256}}"
                }
              ]
            }
            """);
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
