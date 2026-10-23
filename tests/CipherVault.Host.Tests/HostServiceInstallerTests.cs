using System.Diagnostics;
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
            ["HostService", "AdminUi", "AdminCli", "AuthBroker", "McpBridge"],
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
    public async Task FailedServiceStartRollsBackRegistrationFilesAndDirectories()
    {
        using var fixture = new InstallFixture();
        var runner = new ScriptedCommandRunner(
            ("start", new WindowsServiceCommandResult(5, "", "denied")));
        var installer = CreateInstaller(
            fixture.Paths,
            runner,
            new SequenceServiceQuery(
                new WindowsServiceSnapshot(false, "notInstalled", null),
                new WindowsServiceSnapshot(true, "stopped", "Automatic")));

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => installer.InstallAsync(
                CreateOptions(fixture.Paths, startService: true),
                fixture.Paths));

        Assert.Equal(VaultErrorCode.StorageError, exception.Code);
        Assert.Contains(
            runner.Commands,
            command => command == "stop CipherVaultHost");
        Assert.Contains(
            runner.Commands,
            command => command == "delete CipherVaultHost");
        Assert.False(File.Exists(fixture.Paths.ServiceConfigPath));
        Assert.False(File.Exists(fixture.Paths.PublicManifestPath));
        Assert.False(File.Exists(fixture.Paths.ModePath));
        Assert.False(Directory.Exists(fixture.Paths.DataRoot));
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
    public async Task InstallReinstallsValidatedStrictLayoutWithoutTouchingVault()
    {
        using var fixture = new InstallFixture();
        var runner = new ScriptedCommandRunner();
        var installer = CreateInstaller(fixture.Paths, runner);
        var options = CreateOptions(fixture.Paths, startService: false);
        var first = await installer.InstallAsync(options, fixture.Paths);
        var marker = Path.Combine(fixture.Paths.VaultPath, "vault.db");
        File.WriteAllText(marker, "keep");

        var second = await installer.InstallAsync(options, fixture.Paths);

        Assert.NotEqual(first.InstallId, second.InstallId);
        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.Equal(
            second.InstallId,
            CipherVaultServiceConfiguration.LoadInstallManifest(
                fixture.Paths.PublicManifestPath).InstallId);
        Assert.Equal(
            second.InstallId,
            VaultModeStore.Read(fixture.Paths.ModePath).InstallId);
        Assert.Equal(
            2,
            runner.Commands.Count(command => command.StartsWith(
                "create CipherVaultHost",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FailedReinstallRestoresPreviousInstallState()
    {
        using var fixture = new InstallFixture();
        var runner = new ScriptedCommandRunner();
        var firstInstaller = CreateInstaller(fixture.Paths, runner);
        var options = CreateOptions(fixture.Paths, startService: false);
        _ = await firstInstaller.InstallAsync(options, fixture.Paths);
        var marker = Path.Combine(fixture.Paths.VaultPath, "vault.db");
        File.WriteAllText(marker, "keep");
        var previousConfig = File.ReadAllBytes(fixture.Paths.ServiceConfigPath);
        var previousManifest = File.ReadAllBytes(
            fixture.Paths.PublicManifestPath);
        var previousMode = File.ReadAllBytes(fixture.Paths.ModePath);
        var failingInstaller = CreateInstaller(
            fixture.Paths,
            new ScriptedCommandRunner(
                ("start", new WindowsServiceCommandResult(5, "", "denied"))),
            new SequenceServiceQuery(
                new WindowsServiceSnapshot(false, "notInstalled", null),
                new WindowsServiceSnapshot(true, "stopped", "Automatic")));

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => failingInstaller.InstallAsync(
                options with
                {
                    StartService = true
                },
                fixture.Paths));

        Assert.Equal(VaultErrorCode.StorageError, exception.Code);
        Assert.Equal(
            previousConfig,
            File.ReadAllBytes(fixture.Paths.ServiceConfigPath));
        Assert.Equal(
            previousManifest,
            File.ReadAllBytes(fixture.Paths.PublicManifestPath));
        Assert.Equal(
            previousMode,
            File.ReadAllBytes(fixture.Paths.ModePath));
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public async Task InstallRejectsReusingLayoutOwnedByAnotherUser()
    {
        using var fixture = new InstallFixture();
        var runner = new ScriptedCommandRunner();
        var installer = CreateInstaller(fixture.Paths, runner);
        var options = CreateOptions(fixture.Paths, startService: false);
        _ = await installer.InstallAsync(options, fixture.Paths);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => installer.InstallAsync(
                options with
                {
                    OwnerSid = "S-1-5-21-2"
                },
                fixture.Paths));

        Assert.Equal(VaultErrorCode.AlreadyExists, exception.Code);
        Assert.Single(
            runner.Commands,
            command => command.StartsWith(
                "create CipherVaultHost",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallRejectsIncompleteExistingStrictLayout()
    {
        using var fixture = new InstallFixture();
        var runner = new ScriptedCommandRunner();
        var installer = CreateInstaller(fixture.Paths, runner);
        var options = CreateOptions(fixture.Paths, startService: false);
        _ = await installer.InstallAsync(options, fixture.Paths);
        File.Delete(fixture.Paths.ModePath);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => installer.InstallAsync(options, fixture.Paths));

        Assert.Equal(VaultErrorCode.AlreadyExists, exception.Code);
        Assert.Single(
            runner.Commands,
            command => command.StartsWith(
                "create CipherVaultHost",
                StringComparison.Ordinal));
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

    [Fact]
    public async Task UninstallStopsServiceAndRetainsDataByDefault()
    {
        using var fixture = new InstallFixture();
        CreateDataLayout(fixture.Paths);
        var runner = new ScriptedCommandRunner();
        var installer = CreateInstaller(
            fixture.Paths,
            runner,
            new SequenceServiceQuery(
                new WindowsServiceSnapshot(true, "running", "Automatic"),
                new WindowsServiceSnapshot(true, "running", "Automatic"),
                new WindowsServiceSnapshot(true, "running", "Automatic"),
                new WindowsServiceSnapshot(true, "stopped", "Automatic"),
                new WindowsServiceSnapshot(false, "notInstalled", null)));

        var result = await installer.UninstallAsync(
            new HostServiceUninstallOptions(RemoveData: false),
            fixture.Paths);

        Assert.False(result.Installed);
        Assert.False(result.DataRemoved);
        Assert.True(result.DataRetained);
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.VaultPath, "vault.db")));
        Assert.Equal(
            ["stop CipherVaultHost", "delete CipherVaultHost"],
            runner.Commands);
    }

    [Fact]
    public async Task UninstallRemovesDataRootWhenRequested()
    {
        using var fixture = new InstallFixture();
        CreateDataLayout(fixture.Paths);
        var installer = CreateInstaller(
            fixture.Paths,
            new ScriptedCommandRunner());

        var result = await installer.UninstallAsync(
            new HostServiceUninstallOptions(
                RemoveData: true,
                ExpectedDataRoot: fixture.Paths.DataRoot),
            fixture.Paths);

        Assert.False(result.Installed);
        Assert.True(result.DataRemoved);
        Assert.False(result.DataRetained);
        Assert.False(Directory.Exists(fixture.Paths.DataRoot));
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.InstallRoot, "CipherVault.Host.exe")));
    }

    [Fact]
    public async Task UninstallRejectsUnexpectedDataRoot()
    {
        using var fixture = new InstallFixture();
        CreateDataLayout(fixture.Paths);
        var installer = CreateInstaller(
            fixture.Paths,
            new ScriptedCommandRunner());

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => installer.UninstallAsync(
                new HostServiceUninstallOptions(
                    RemoveData: true,
                    ExpectedDataRoot: Path.Combine(fixture.Root, "Other")),
                fixture.Paths));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
        Assert.True(File.Exists(
            Path.Combine(fixture.Paths.VaultPath, "vault.db")));
    }

    [Fact]
    public async Task UninstallDoesNotFollowJunctionsInsideDataRoot()
    {
        using var fixture = new InstallFixture();
        CreateDataLayout(fixture.Paths);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        var junction = Path.Combine(fixture.Paths.DataRoot, "linked");
        CreateJunction(junction, outside);
        var installer = CreateInstaller(
            fixture.Paths,
            new ScriptedCommandRunner());

        await installer.UninstallAsync(
            new HostServiceUninstallOptions(
                RemoveData: true,
                ExpectedDataRoot: fixture.Paths.DataRoot),
            fixture.Paths);

        Assert.True(File.Exists(marker));
        Assert.False(Directory.Exists(fixture.Paths.DataRoot));
    }

    [Fact]
    public async Task UninstallRejectsReparseDataRoot()
    {
        using var fixture = new InstallFixture();
        CreateDataLayout(fixture.Paths);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        Directory.Delete(fixture.Paths.DataRoot, recursive: true);
        CreateJunction(fixture.Paths.DataRoot, outside);
        var installer = CreateInstaller(
            fixture.Paths,
            new ScriptedCommandRunner());

        try
        {
            var exception = await Assert.ThrowsAsync<VaultException>(
                () => installer.UninstallAsync(
                    new HostServiceUninstallOptions(
                        RemoveData: true,
                        ExpectedDataRoot: fixture.Paths.DataRoot),
                    fixture.Paths));

            Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(fixture.Paths.DataRoot) &&
                (File.GetAttributes(fixture.Paths.DataRoot) &
                 FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(fixture.Paths.DataRoot);
            }
        }
    }

    private static void CreateDataLayout(CipherVaultServicePaths paths)
    {
        Directory.CreateDirectory(paths.VaultPath);
        Directory.CreateDirectory(paths.LogsPath);
        Directory.CreateDirectory(paths.LocksPath);
        Directory.CreateDirectory(paths.TransferRoot);
        File.WriteAllText(
            Path.Combine(paths.VaultPath, "vault.db"),
            "vault");
        File.WriteAllText(paths.ServiceConfigPath, "{}");
        File.WriteAllText(paths.PublicManifestPath, "{}");
        File.WriteAllText(paths.ModePath, "{}");
    }

    private static void CreateJunction(string path, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start cmd.exe.");
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Unable to create junction: {standardError}");
    }

    private static HostServiceInstaller CreateInstaller(
        CipherVaultServicePaths paths,
        IWindowsServiceCommandRunner runner,
        IWindowsServiceQuery? query = null)
    {
        return new HostServiceInstaller(
            query ?? new AlwaysNotInstalledQuery(),
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
            Path.Combine(paths.InstallRoot, "CipherVault.Mcp.exe"),
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
            File.WriteAllBytes(
                Path.Combine(Paths.InstallRoot, "CipherVault.Mcp.exe"),
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

    private sealed class SequenceServiceQuery(
        params WindowsServiceSnapshot[] snapshots) : IWindowsServiceQuery
    {
        private readonly Queue<WindowsServiceSnapshot> _snapshots =
            new(snapshots);

        public WindowsServiceSnapshot GetStatus(string serviceName)
        {
            return _snapshots.Count > 0
                ? _snapshots.Dequeue()
                : throw new InvalidOperationException(
                    "No service status snapshot was queued.");
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
