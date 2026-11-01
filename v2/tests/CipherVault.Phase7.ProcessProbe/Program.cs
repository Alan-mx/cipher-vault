using CipherVault.Application.Backup;
using CipherVault.Application.Documents;
using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Core.Documents;
using CipherVault.Server.Hosting;

namespace CipherVault.Phase7.ProcessProbe;

internal static class Program
{
    private const string Marker = "phase7-cross-platform-marker";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["create", var vaultDirectory, var backupDirectory, var outputPath, var password] =>
                    await CreateAsync(
                        vaultDirectory,
                        backupDirectory,
                        outputPath,
                        password),
                ["restore", var packagePath, var targetDirectory, var password] =>
                    await RestoreAsync(
                        packagePath,
                        targetDirectory,
                        password),
                ["verify", var vaultDirectory, var password] =>
                    await VerifyAsync(vaultDirectory, password),
                _ => Usage()
            };
        }
        catch
        {
            Console.Error.WriteLine("phase7_probe_failed");
            return 1;
        }
    }

    private static async Task<int> CreateAsync(
        string vaultDirectory,
        string backupDirectory,
        string outputPath,
        string password)
    {
        await using var manager = await VaultSessionManager.CreateAsync(
            vaultDirectory);
        _ = await manager.InitializeAsync(password);
        _ = await manager.UnlockAsync(password);
        var documents = new DocumentService(manager);
        _ = await documents.CreateAsync(
            new CreateDocumentRequest(
                "Phase 7 Cross Platform",
                "Phase7/CrossPlatform.md",
                null,
                VaultDocumentFormat.Markdown,
                $"# Cross Platform\n\n{Marker}",
                null),
            new VaultActorContext(VaultActorKind.UserUi));
        var backups = new BackupService(
            new BackupStorageOptions(backupDirectory),
            manager,
            TimeProvider.System);
        var result = await backups.CreateAsync(
            new VaultActorContext(VaultActorKind.UserUi));
        if (!result.AuditRecorded
            || result.Item.Status != BackupFileStatus.Verified)
        {
            return 1;
        }

        var packagePath = Path.Combine(
            backupDirectory,
            result.Item.FileName);
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.Copy(packagePath, outputPath, overwrite: true);
        Console.Out.WriteLine("phase7_probe_created");
        return 0;
    }

    private static async Task<int> RestoreAsync(
        string packagePath,
        string targetDirectory,
        string password)
    {
        var targetParent = Path.GetDirectoryName(
            Path.GetFullPath(targetDirectory))
            ?? throw new InvalidOperationException();
        Directory.CreateDirectory(targetParent);
        var workRoot = Path.Combine(
            targetParent,
            $".phase7-restore-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var paths = ApplicationPaths.Create(
                Path.Combine(workRoot, "data"),
                Path.Combine(workRoot, "config"),
                Path.Combine(workRoot, "backups"));
            var options = new ServerOptions(
                paths,
                [new ServerListenAddress("127.0.0.1", 43130)],
                TimeSpan.FromMinutes(10),
                ServerOptions.DefaultMaxRequestBodyBytes);
            var terminal = new ProbeTerminal(password);
            var exitCode = await new RestoreMaintenanceCommand().RunAsync(
                options,
                new RestoreCommandOptions(packagePath, targetDirectory),
                terminal,
                CancellationToken.None);
            if (exitCode != RestoreExitCodes.Success)
            {
                Console.Error.WriteLine(
                    terminal.LastError?.SafeMessage
                        ?? "phase7_probe_failed");
            }
            else
            {
                Console.Out.WriteLine("phase7_probe_restored");
            }
            return exitCode;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                {
                    Directory.Delete(workRoot, recursive: true);
                }
            }
            catch
            {
                // Probe work data is encrypted and outside the published target.
            }
        }
    }

    private static async Task<int> VerifyAsync(
        string vaultDirectory,
        string password)
    {
        await using var manager = await VaultSessionManager.CreateAsync(
            vaultDirectory);
        var status = await manager.GetStatusAsync();
        if (status.State != VaultSessionState.Locked)
        {
            return 1;
        }
        _ = await manager.UnlockAsync(password);
        var documents = new DocumentService(manager);
        var catalog = await documents.ListAsync(
            new CatalogQuery(
                null,
                VaultDocumentFormat.Markdown,
                new CursorPageRequest(100)));
        var item = catalog.Items.Single(document =>
            document.VirtualPath == "Phase7/CrossPlatform.md");
        var content = await documents.ReadContentAsync(
            item.DocumentId,
            0,
            64 * 1024);
        if (!content.Content.Contains(Marker, StringComparison.Ordinal))
        {
            return 1;
        }
        Console.Out.WriteLine("phase7_probe_verified");
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("phase7_probe_usage");
        return 2;
    }

    private sealed class ProbeTerminal : IInteractiveConsole
    {
        private readonly string password;

        public ProbeTerminal(string password)
        {
            this.password = password;
        }

        public bool IsInteractive => true;

        public RestoreOutcome? LastError { get; private set; }

        public Task<string?> ReadPasswordAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(password);

        public void WriteSafeError(RestoreOutcome outcome)
        {
            LastError = outcome;
        }

        public void WriteSafeSuccess(RestoreVerificationResult result)
        {
        }
    }
}
