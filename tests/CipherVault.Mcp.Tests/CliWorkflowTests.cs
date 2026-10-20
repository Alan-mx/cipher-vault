using System.Diagnostics;
using System.Text.Json;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Mcp.Tests;

public sealed class CliWorkflowTests
{
    private const string Password = "cli workflow password";

    [Fact]
    public async Task BackupExportAndRestoreWorkThroughRealCliProcess()
    {
        await using var fixture = await CliWorkflowFixture.CreateAsync();
        var cliPath = FindCliExecutable();

        var backup = await RunCliAsync(
            cliPath,
            "backup",
            "--config",
            fixture.ConfigPath,
            "--to",
            fixture.BackupPath);
        Assert.True(backup.ExitCode == 0, FormatFailure(backup));
        Assert.True(File.Exists(fixture.BackupPath));

        var export = await RunCliAsync(
            cliPath,
            "export",
            "--config",
            fixture.ConfigPath,
            "--to",
            fixture.ExportPath);
        Assert.True(export.ExitCode == 0, FormatFailure(export));
        Assert.True(File.Exists(
            Path.Combine(fixture.ExportPath, "vault-export-manifest.json")));
        Assert.True(File.Exists(
            Path.Combine(fixture.ExportPath, "workflow", "CLI workflow.md")));

        var restore = await RunCliAsync(
            cliPath,
            "restore",
            "--config",
            fixture.ConfigPath,
            "--backup",
            fixture.BackupPath,
            "--to",
            fixture.RestoredPath,
            "--credential",
            "password");
        Assert.True(restore.ExitCode == 0, FormatFailure(restore));

        await using var restoredService = new VaultService(
            new AlwaysActiveActivityProvider());
        await restoredService.UnlockAsync(
            fixture.RestoredPath,
            Password,
            VaultCredentialType.Password);
        var catalog = await restoredService.ListCatalogAsync(null, null, 10);
        Assert.Contains(catalog.Items, item => item.Title == "CLI workflow");
        Assert.True((await restoredService.VerifyVaultAsync()).Valid);
    }

    private static async Task<ProcessResult> RunCliAsync(
        string cliPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var variable in CipherVaultEnvironmentVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start CipherVault.Cli.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.WriteLine(Password);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("CipherVault.Cli.exe did not exit within 30 seconds.");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string FormatFailure(ProcessResult result)
    {
        return $"stdout: {result.Stdout}{Environment.NewLine}stderr: {result.Stderr}";
    }

    private static string FindCliExecutable()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name
            ?? throw new InvalidOperationException("Unable to resolve test build configuration.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var executable = Path.Combine(
            repositoryRoot,
            "src",
            "CipherVault.Cli",
            "bin",
            configuration,
            "net10.0-windows",
            "CipherVault.Cli.exe");
        Assert.True(File.Exists(executable), $"CLI executable was not found: {executable}");
        return executable;
    }

    private static readonly string[] CipherVaultEnvironmentVariables =
    [
        "CIPHER_VAULT_CONFIG",
        "CIPHER_VAULT_PATH",
        "CIPHER_VAULT_IDLE_MINUTES",
        "CIPHER_VAULT_HARD_HOURS",
        "CIPHER_VAULT_ACCESS_MODE",
        "CIPHER_VAULT_READ_ONLY"
    ];

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class CliWorkflowFixture : IAsyncDisposable
    {
        private CliWorkflowFixture(
            string rootPath,
            string vaultPath,
            string configPath)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            ConfigPath = configPath;
            BackupPath = Path.Combine(rootPath, "vault.cvbackup");
            ExportPath = Path.Combine(rootPath, "export");
            RestoredPath = Path.Combine(rootPath, "restored");
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public string ConfigPath { get; }
        public string BackupPath { get; }
        public string ExportPath { get; }
        public string RestoredPath { get; }

        public static async Task<CliWorkflowFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-cli-workflow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var configPath = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(
                configPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 1,
                    vaultPath,
                    session = new
                    {
                        idleMinutes = 15,
                        hardHours = 4
                    }
                }));

            await using var service = new VaultService(
                new AlwaysActiveActivityProvider());
            var initialized = await service.InitializeAsync(vaultPath, Password);
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            await service.UnlockAsync(
                vaultPath,
                Password,
                VaultCredentialType.Password);
            await service.CreateNoteAsync(
                "CLI workflow",
                "workflow",
                "md",
                "content for CLI backup and export",
                null,
                VaultActorKind.UserUi);
            return new CliWorkflowFixture(root, vaultPath, configPath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
