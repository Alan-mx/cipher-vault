using System.Net;
using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Tests;

public sealed class BackupRestoreCommandTests
{
    [Fact]
    public void CommandLineParserIsStrict()
    {
        Assert.Equal(
            ServerRunMode.Normal,
            ServerCommandLine.Parse([]).Mode);
        Assert.Equal(
            ServerRunMode.Restore,
            ServerCommandLine.Parse(
                ["--restore-backup", "a.cvbackup", "--target", "out"]).Mode);
        Assert.Equal(
            ServerRunMode.Restore,
            ServerCommandLine.Parse(
                ["--target", "out", "--restore-backup", "a.cvbackup"]).Mode);

        var invalid = new[]
        {
            new[] { "--target", "out" },
            new[] { "--restore-backup", "a", "--target", "b", "extra" },
            new[] { "--restore-backup", "a", "--restore-backup", "b", "--target", "c" },
            new[] { "--restore-backup", "a", "--target" },
            new[] { "--unknown", "a", "--target", "b" }
        };
        Assert.All(
            invalid,
            args => Assert.Equal(
                ServerRunMode.Invalid,
                ServerCommandLine.Parse(args).Mode));
    }

    [Fact]
    public async Task RestoreMaintenanceCreatesLockedVaultFromBackup()
    {
        var external = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-restore-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        string? packagePath = null;
        try
        {
            packagePath = await CreatePackageAsync(external);

            var restoreRoot = Path.Combine(external, "restore-context");
            Directory.CreateDirectory(restoreRoot);
            var paths = ApplicationPaths.Create(
                Path.Combine(restoreRoot, "data"),
                Path.Combine(restoreRoot, "config"),
                Path.Combine(restoreRoot, "backups"));
            var target = Path.Combine(restoreRoot, "restored");
            var options = new ServerOptions(
                paths,
                [new ServerListenAddress("127.0.0.1", 43128)],
                TimeSpan.FromMinutes(10),
                ServerOptions.DefaultMaxRequestBodyBytes);
            var terminal = new FakeInteractiveConsole(
                ServerTestApplication.Password);
            var command = new RestoreMaintenanceCommand();
            await using (RestoreTargetLock.Acquire(target))
            {
            }

            var exitCode = await command.RunAsync(
                options,
                new RestoreCommandOptions(packagePath, target),
                terminal,
                CancellationToken.None);

            Assert.True(
                exitCode == RestoreExitCodes.Success,
                terminal.LastError?.SafeMessage);
            Assert.Equal(
                ["vault.db", "vault.key"],
                Directory.GetFileSystemEntries(target)
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            Assert.False(Directory.EnumerateFileSystemEntries(
                Path.GetDirectoryName(target)!,
                "*.partial",
                SearchOption.TopDirectoryOnly).Any());
            await using var manager = await VaultSessionManager.CreateAsync(
                target);
            Assert.Equal(VaultSessionState.Locked, manager.State);
            var status = await manager.UnlockAsync(
                ServerTestApplication.Password);
            Assert.Equal(VaultSessionState.Unlocked, status.State);
        }
        finally
        {
            if (Directory.Exists(external))
            {
                Directory.Delete(external, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoreWithWrongPasswordLeavesNoTarget()
    {
        var external = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-restore-wrong-password-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        try
        {
            var package = await CreatePackageAsync(external);
            var restoreRoot = Path.Combine(external, "restore-context");
            Directory.CreateDirectory(restoreRoot);
            var paths = ApplicationPaths.Create(
                Path.Combine(restoreRoot, "data"),
                Path.Combine(restoreRoot, "config"),
                Path.Combine(restoreRoot, "backups"));
            var target = Path.Combine(restoreRoot, "restored");
            var options = new ServerOptions(
                paths,
                [new ServerListenAddress("127.0.0.1", 43131)],
                TimeSpan.FromMinutes(10),
                ServerOptions.DefaultMaxRequestBodyBytes);
            var terminal = new FakeInteractiveConsole(
                "wrong backup password");

            var exitCode = await new RestoreMaintenanceCommand().RunAsync(
                options,
                new RestoreCommandOptions(package, target),
                terminal,
                CancellationToken.None);

            Assert.Equal(RestoreExitCodes.UnlockFailed, exitCode);
            Assert.Equal(
                RestoreFailureCode.UnlockFailed,
                terminal.LastError?.FailureCode);
            Assert.False(Directory.Exists(target));
            Assert.False(Directory.EnumerateFileSystemEntries(
                restoreRoot,
                "*.partial",
                SearchOption.TopDirectoryOnly).Any());
        }
        finally
        {
            if (Directory.Exists(external))
            {
                Directory.Delete(external, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoreRejectsNonInteractiveTerminalBeforeWrites()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-restore-tty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "backup.cvbackup");
            await File.WriteAllBytesAsync(package, [1, 2, 3]);
            var paths = ApplicationPaths.Create(
                Path.Combine(root, "data"),
                Path.Combine(root, "config"),
                Path.Combine(root, "backups"));
            var target = Path.Combine(root, "restored");
            var options = new ServerOptions(
                paths,
                [new ServerListenAddress("127.0.0.1", 43129)],
                TimeSpan.FromMinutes(10),
                ServerOptions.DefaultMaxRequestBodyBytes);
            var terminal = new FakeInteractiveConsole(
                ServerTestApplication.Password,
                interactive: false);

            var exitCode = await new RestoreMaintenanceCommand().RunAsync(
                options,
                new RestoreCommandOptions(package, target),
                terminal,
                CancellationToken.None);

            Assert.Equal(
                RestoreExitCodes.NotInteractiveTerminal,
                exitCode);
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeInteractiveConsole : IInteractiveConsole
    {
        private readonly string password;

        public FakeInteractiveConsole(
            string password,
            bool interactive = true)
        {
            this.password = password;
            IsInteractive = interactive;
        }

        public bool IsInteractive { get; }

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

    private static async Task<string> CreatePackageAsync(
        string external)
    {
        await using var source = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await source.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var antiforgery = await source.Client.GetAntiforgeryTokenAsync();
        using var created = await source.Client.PostWithAntiforgeryAsync(
            "/api/backups",
            antiforgery);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var fileName = json.RootElement
            .GetProperty("backup")
            .GetProperty("fileName")
            .GetString()!;
        var packagePath = Path.Combine(external, fileName);
        File.Copy(
            Path.Combine(
                source.Options.Paths.BackupDirectory,
                fileName),
            packagePath);
        return packagePath;
    }
}
