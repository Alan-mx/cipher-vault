using System.Diagnostics;
using System.Text.Json;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Security;
using CipherVault.Core.Session;

namespace CipherVault.Mcp.Tests;

public sealed class CliMultiProcessTests
{
    private const string Password = "multi-process-password";

    [Fact]
    public async Task StandaloneCliRejectsVaultAlreadyOwnedByAnotherProcess()
    {
        await using var fixture = await CliProcessFixture.CreateAsync();
        var cliPath = FindCliExecutable();
        await using var ownership = await VaultOwnershipLock.AcquireAsync(
            fixture.VaultPath);

        var result = await RunCliAsync(
            cliPath,
            fixture.ConfigPath,
            fixture.FirstJobId);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("already in use", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProcessResult> RunCliAsync(
        string cliPath,
        string configPath,
        Guid jobId)
    {
        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("import-apply");
        startInfo.ArgumentList.Add("--standalone");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--job");
        startInfo.ArgumentList.Add(jobId.ToString("D"));
        foreach (var variable in CipherVaultEnvironmentVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start CipherVault.Cli.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(Password);
        }
        catch (IOException)
        {
        }
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

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

    private sealed class CliProcessFixture : IAsyncDisposable
    {
        private CliProcessFixture(
            string rootPath,
            string vaultPath,
            string configPath,
            Guid firstJobId,
            Guid secondJobId)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            ConfigPath = configPath;
            FirstJobId = firstJobId;
            SecondJobId = secondJobId;
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public string ConfigPath { get; }
        public Guid FirstJobId { get; }
        public Guid SecondJobId { get; }

        public static async Task<CliProcessFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-cli-process-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var configPath = Path.Combine(root, "config.json");
            var firstSource = Path.Combine(root, "source-one");
            var secondSource = Path.Combine(root, "source-two");
            Directory.CreateDirectory(firstSource);
            Directory.CreateDirectory(secondSource);
            await File.WriteAllTextAsync(
                Path.Combine(firstSource, "process one.md"),
                "content from process one");
            await File.WriteAllTextAsync(
                Path.Combine(secondSource, "process two.md"),
                "content from process two");
            await File.WriteAllTextAsync(
                configPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 2,
                    vaultPath,
                    session = new
                    {
                        idleMinutes = 15,
                        hardHours = 4
                    }
                }));

            var service = new VaultService(new AlwaysActiveActivityProvider());
            var succeeded = false;
            try
            {
                var initialized = await service.InitializeAsync(vaultPath, Password);
                await service.ConfirmRecoveryKeyAsync(
                    vaultPath,
                    initialized.RecoveryKey,
                    VaultActorKind.UserUi);
                await service.UnlockAsync(
                    vaultPath,
                    Password,
                    VaultCredentialType.Password);
                var firstPreview = await service.PreviewImportAsync(
                    firstSource,
                    VaultActorKind.UserCli);
                var secondPreview = await service.PreviewImportAsync(
                    secondSource,
                    VaultActorKind.UserCli);
                succeeded = true;
                return new CliProcessFixture(
                    root,
                    vaultPath,
                    configPath,
                    firstPreview.JobId,
                    secondPreview.JobId);
            }
            finally
            {
                await service.DisposeAsync();
                if (!succeeded && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
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
