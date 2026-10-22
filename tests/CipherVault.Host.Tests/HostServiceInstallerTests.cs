using System.IO;
using System.Security.Principal;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Service;
using CipherVault.Host.ServiceManagement;

namespace CipherVault.Host.Tests;

public sealed class HostServiceInstallerTests
{
    private const string SignerThumbprint =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ImageSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task InstallCreatesStrictLayoutAndRegistersService()
    {
        using var fixture = new InstallFixture();
        var runner = new ScriptedCommandRunner();
        var installer = CreateInstaller(fixture.Paths, runner);

        var result = await installer.InstallAsync(
            CreateOptions(fixture.Paths, startService: false),
            fixture.Paths);

        var configuration = CipherVaultServiceConfiguration.Load(
            fixture.Paths.ServiceConfigPath);
        var publicManifest = CipherVaultServiceConfiguration.LoadInstallManifest(
            fixture.Paths.PublicManifestPath);
        var mode = VaultModeStore.Read(fixture.Paths.ModePath);
        Assert.Equal("strict", configuration.SecurityMode);
        Assert.Equal(
            "HostService",
            configuration.ProcessPolicy!.AllowedImages![0].Kind);
        Assert.Equal(
            ["HostService", "AdminUi", "AdminCli", "AuthBroker"],
            configuration.ProcessPolicy.AllowedImages
                .Select(image => image.Kind));
        Assert.Equal(result.InstallId, publicManifest.InstallId);
        Assert.Equal(result.InstallId, mode.InstallId);
        Assert.Equal(
            result.ServiceSid,
            publicManifest.ServiceSid);
        Assert.False(result.Started);
        Assert.Contains(
            runner.Commands,
            command => command.StartsWith(
                "create CipherVaultHost",
                StringComparison.Ordinal));
        Assert.Contains(
            runner.Commands,
            command => command == "sidtype CipherVaultHost unrestricted");
        Assert.Contains(
            runner.Commands,
            command => command == "description CipherVaultHost CipherVault Host");
    }

    [Fact]
    public async Task FailedServiceConfigurationRollsBackRegistrationAndFiles()
    {
        using var fixture = new InstallFixture();
        var runner = new ScriptedCommandRunner(
            ("sidtype", new WindowsServiceCommandResult(5, "", "denied")));
        var installer = CreateInstaller(fixture.Paths, runner);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => installer.InstallAsync(
                CreateOptions(fixture.Paths, startService: false),
                fixture.Paths));

        Assert.Equal(VaultErrorCode.StorageError, exception.Code);
        Assert.Contains(
            runner.Commands,
            command => command == "delete CipherVaultHost");
        Assert.False(File.Exists(fixture.Paths.ServiceConfigPath));
        Assert.False(File.Exists(fixture.Paths.PublicManifestPath));
        Assert.False(File.Exists(fixture.Paths.ModePath));
    }

    [Fact]
    public async Task InstallRejectsExistingVaultData()
    {
        using var fixture = new InstallFixture();
        Directory.CreateDirectory(fixture.Paths.VaultPath);
        File.WriteAllText(
            Path.Combine(fixture.Paths.VaultPath, "vault.db"),
            "existing");
        var runner = new ScriptedCommandRunner();
        var installer = CreateInstaller(fixture.Paths, runner);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => installer.InstallAsync(
                CreateOptions(fixture.Paths, startService: false),
                fixture.Paths));

        Assert.Equal(VaultErrorCode.AlreadyExists, exception.Code);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task InstallRejectsImageOutsideInstallRoot()
    {
        using var fixture = new InstallFixture();
        var outside = Path.Combine(fixture.Root, "outside.exe");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var installer = CreateInstaller(
            fixture.Paths,
            new ScriptedCommandRunner());

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => installer.InstallAsync(
                CreateOptions(
                    fixture.Paths,
                    startService: false) with
                {
                    HostExecutablePath = outside
                },
                fixture.Paths));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private static HostServiceInstaller CreateInstaller(
        CipherVaultServicePaths paths,
        IWindowsServiceCommandRunner runner)
    {
        return new HostServiceInstaller(
            new AlwaysNotInstalledQuery(),
            runner,
            new StaticServiceSidResolver(),
            new StaticImageInspector(),
            new RecordingAcl());
    }

    private static HostServiceInstallOptions CreateOptions(
        CipherVaultServicePaths paths,
        bool startService)
    {
        return new HostServiceInstallOptions(
            "S-1-5-21-1",
            SignerThumbprint,
            Path.Combine(paths.InstallRoot, "CipherVault.Host.exe"),
            Path.Combine(paths.InstallRoot, "CipherVault.Ui.Wpf.exe"),
            Path.Combine(paths.InstallRoot, "CipherVault.Cli.exe"),
            Path.Combine(paths.InstallRoot, "CipherVault.Auth.Broker.exe"),
            McpExecutablePath: null,
            StartService: startService);
    }

    private sealed class InstallFixture : IDisposable
    {
        public InstallFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-install-{Guid.NewGuid():N}");
            Paths = CipherVaultServicePaths.Create(
                Path.Combine(Root, "ProgramFiles"),
                Path.Combine(Root, "ProgramData"),
                Path.Combine(Root, "LocalAppData"));
            Directory.CreateDirectory(Paths.InstallRoot);
            File.WriteAllBytes(
                Path.Combine(Paths.InstallRoot, "CipherVault.Host.exe"),
                [1, 2, 3, 4]);
            File.WriteAllBytes(
                Path.Combine(Paths.InstallRoot, "CipherVault.Ui.Wpf.exe"),
                [1, 2, 3, 4]);
            File.WriteAllBytes(
                Path.Combine(Paths.InstallRoot, "CipherVault.Cli.exe"),
                [1, 2, 3, 4]);
            File.WriteAllBytes(
                Path.Combine(
                    Paths.InstallRoot,
                    "CipherVault.Auth.Broker.exe"),
                [1, 2, 3, 4]);
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

    private sealed class AlwaysNotInstalledQuery : IWindowsServiceQuery
    {
        public WindowsServiceSnapshot GetStatus(string serviceName)
        {
            return new WindowsServiceSnapshot(
                false,
                "notInstalled",
                null);
        }
    }

    private sealed class StaticServiceSidResolver : IServiceSidResolver
    {
        public SecurityIdentifier Resolve(string serviceName)
        {
            Assert.Equal(StrictServiceHost.ServiceName, serviceName);
            return new SecurityIdentifier("S-1-5-80-1");
        }
    }

    private sealed class StaticImageInspector : IServiceImageInspector
    {
        public ServiceImageEvidence Inspect(string imagePath)
        {
            Assert.True(File.Exists(imagePath));
            return new ServiceImageEvidence(
                ImageSha256,
                SignerThumbprint,
                "CN=CipherVault Test");
        }
    }

    private sealed class RecordingAcl : IServiceInstallAcl
    {
        public void Apply(
            CipherVaultServicePaths paths,
            SecurityIdentifier serviceSid,
            SecurityIdentifier ownerSid)
        {
            Directory.CreateDirectory(paths.DataRoot);
            Directory.CreateDirectory(paths.VaultPath);
            Directory.CreateDirectory(paths.LogsPath);
            Directory.CreateDirectory(paths.LocksPath);
            Directory.CreateDirectory(paths.TransferRoot);
        }
    }

    private sealed class ScriptedCommandRunner(
        params (string Prefix, WindowsServiceCommandResult Result)[] overrides)
        : IWindowsServiceCommandRunner
    {
        public List<string> Commands { get; } = [];

        public Task<WindowsServiceCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            var command = string.Join(' ', arguments);
            Commands.Add(command);
            var match = overrides.FirstOrDefault(
                item => command.StartsWith(
                    item.Prefix,
                    StringComparison.Ordinal));
            return Task.FromResult(
                match.Result ??
                new WindowsServiceCommandResult(0, string.Empty, string.Empty));
        }
    }
}
