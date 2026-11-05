using CipherVault.Application.Vault;
using CipherVault.Core.Backup;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Tests;

public sealed class BackupUnsupportedSchemaTests
{
    [Fact]
    public async Task RestoreRejectsUnsupportedSchemaWithoutPublishingTarget()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-restore-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "BackupUnsupportedSchema",
                "unsupported-schema-5-format-1.cvbackup");
            Assert.True(File.Exists(package), package);
            var paths = ApplicationPaths.Create(
                Path.Combine(root, "data"),
                Path.Combine(root, "config"),
                Path.Combine(root, "backups"));
            var target = Path.Combine(root, "restored");
            var options = new ServerOptions(
                paths,
                [new ServerListenAddress("127.0.0.1", 43132)],
                TimeSpan.FromMinutes(10),
                ServerOptions.DefaultMaxRequestBodyBytes);
            var terminal = new FakeInteractiveConsole(
                "correct horse battery staple");

            var exitCode = await new RestoreMaintenanceCommand().RunAsync(
                options,
                new RestoreCommandOptions(package, target),
                terminal,
                CancellationToken.None);

            Assert.Equal(RestoreExitCodes.DatabaseInvalid, exitCode);
            Assert.Equal(
                RestoreFailureCode.SchemaUnsupported,
                terminal.LastError?.FailureCode);
            Assert.False(Directory.Exists(target));
            Assert.False(Directory.EnumerateFileSystemEntries(
                root,
                "*.partial",
                SearchOption.TopDirectoryOnly).Any());
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

        public FakeInteractiveConsole(string password)
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
