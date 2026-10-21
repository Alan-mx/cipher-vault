using System.Diagnostics;
using System.Text.Json;
using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Mcp.Tests;

public sealed class CliCrashRecoveryTests
{
    private const string Password = "crash-recovery-password";
    private const int ImportFileCount = 50;

    [Fact]
    public async Task InterruptedImportCanResumeWithoutDuplicateDocuments()
    {
        await using var fixture = await CrashFixture.CreateAsync();
        var cliPath = FindCliExecutable();
        using var interrupted = StartImportProcess(
            cliPath,
            fixture.ConfigPath,
            fixture.JobId);

        await WaitForFirstImportedDocumentAsync(fixture.Service);
        interrupted.Process.Kill(entireProcessTree: true);
        await interrupted.Process.WaitForExitAsync();
        Assert.NotEqual(0, interrupted.Process.ExitCode);
        await interrupted.FinishOutputAsync();

        var retry = await RunImportToCompletionAsync(
            cliPath,
            fixture.ConfigPath,
            fixture.JobId);
        Assert.True(
            retry.ExitCode == 0,
            $"stdout: {retry.Stdout}{Environment.NewLine}stderr: {retry.Stderr}");

        var catalog = await ReadAllCatalogItemsAsync(fixture.Service);
        Assert.Equal(ImportFileCount, catalog.Count);
        Assert.Equal(
            ImportFileCount,
            catalog.Select(item => item.DocumentId).Distinct().Count());
        Assert.Equal(
            ImportFileCount,
            catalog.Select(item => item.Title).Distinct().Count());
        Assert.True((await fixture.Service.VerifyVaultAsync()).Valid);

        var auditEvents = await ReadAllAuditEventsAsync(fixture.Service);
        Assert.True(
            auditEvents.Count(item => item.Action == "create") >= ImportFileCount);
    }

    private static async Task WaitForFirstImportedDocumentAsync(VaultService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            var page = await service.GetAuditPageAsync(null, 100, timeout.Token);
            if (page.Items.Any(item => item.Action == "create"))
            {
                return;
            }
            await Task.Delay(20, timeout.Token);
        }
        throw new TimeoutException("No document import was observed before timeout.");
    }

    private static async Task<List<VaultCatalogItem>> ReadAllCatalogItemsAsync(
        VaultService service)
    {
        var items = new List<VaultCatalogItem>();
        string? cursor = null;
        do
        {
            var page = await service.ListCatalogAsync(null, cursor, 100);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return items;
    }

    private static async Task<List<VaultAuditItem>> ReadAllAuditEventsAsync(
        VaultService service)
    {
        var items = new List<VaultAuditItem>();
        string? cursor = null;
        do
        {
            var page = await service.GetAuditPageAsync(cursor, 100);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return items;
    }

    private static ImportProcess StartImportProcess(
        string cliPath,
        string configPath,
        Guid jobId)
    {
        var startInfo = CreateProcessStartInfo(cliPath, configPath, jobId);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start CipherVault.Cli.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.WriteLine(Password);
        process.StandardInput.Close();
        return new ImportProcess(process, stdoutTask, stderrTask);
    }

    private static async Task<ProcessResult> RunImportToCompletionAsync(
        string cliPath,
        string configPath,
        Guid jobId)
    {
        using var import = StartImportProcess(cliPath, configPath, jobId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try
        {
            await import.Process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            import.Process.Kill(entireProcessTree: true);
            await import.Process.WaitForExitAsync();
            var result = await import.FinishOutputAsync();
            throw new TimeoutException(
                "CipherVault.Cli.exe retry did not exit within 180 seconds. " +
                $"ExitCode: {result.ExitCode}.{Environment.NewLine}" +
                $"stdout: {result.Stdout}{Environment.NewLine}" +
                $"stderr: {result.Stderr}");
        }
        return await import.FinishOutputAsync();
    }

    private static ProcessStartInfo CreateProcessStartInfo(
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
        return startInfo;
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

    private sealed class ImportProcess : IDisposable
    {
        private readonly Task<string> _stdoutTask;
        private readonly Task<string> _stderrTask;

        public ImportProcess(
            Process process,
            Task<string> stdoutTask,
            Task<string> stderrTask)
        {
            Process = process;
            _stdoutTask = stdoutTask;
            _stderrTask = stderrTask;
        }

        public Process Process { get; }

        public async Task<ProcessResult> FinishOutputAsync()
        {
            return new ProcessResult(
                Process.ExitCode,
                await _stdoutTask,
                await _stderrTask);
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
            }
            Process.Dispose();
        }
    }

    private sealed class CrashFixture : IAsyncDisposable
    {
        private CrashFixture(
            string rootPath,
            string configPath,
            Guid jobId,
            VaultService service)
        {
            RootPath = rootPath;
            ConfigPath = configPath;
            JobId = jobId;
            Service = service;
        }

        public string RootPath { get; }
        public string ConfigPath { get; }
        public Guid JobId { get; }
        public VaultService Service { get; }

        public static async Task<CrashFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-crash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var sourcePath = Path.Combine(root, "source");
            var configPath = Path.Combine(root, "config.json");
            Directory.CreateDirectory(sourcePath);
            for (var index = 0; index < ImportFileCount; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(sourcePath, $"document-{index:D4}.md"),
                    $"content {index}{Environment.NewLine}{new string('x', 4096)}");
            }
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
                var preview = await service.PreviewImportAsync(
                    sourcePath,
                    VaultActorKind.UserCli);
                succeeded = true;
                return new CrashFixture(root, configPath, preview.JobId, service);
            }
            finally
            {
                if (!succeeded)
                {
                    await service.DisposeAsync();
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
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
