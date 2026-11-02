using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Server.Hosting;

public sealed record ApplicationPaths(
    string VaultDirectory,
    string ConfigDirectory,
    string BackupDirectory,
    string DataProtectionDirectory,
    string ServerLockPath,
    string McpTokenPath,
    string GitBackupStatePath,
    string BackupLockPath,
    string GitHooksDirectory,
    string GitAttributesFile)
{
    public static ApplicationPaths Create(
        string vaultDirectory,
        string configDirectory,
        string? backupDirectory = null)
    {
        var vault = Canonicalize(vaultDirectory, "Vault directory");
        var config = Canonicalize(configDirectory, "Config directory");
        if (string.Equals(vault, config, PathComparison))
        {
            throw InvalidPaths("Vault and config directories must be different.");
        }

        var relativeConfig = Path.GetRelativePath(vault, config);
        if (relativeConfig == "."
            || (!Path.IsPathRooted(relativeConfig)
                && relativeConfig != ".."
                && !relativeConfig.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    PathComparison)))
        {
            throw InvalidPaths("Config directory cannot be inside the Vault directory.");
        }

        var backup = string.IsNullOrWhiteSpace(backupDirectory)
            ? Path.Combine(
                Path.GetDirectoryName(vault)
                    ?? throw InvalidPaths("Vault directory has no parent directory."),
                "backups")
            : Canonicalize(
                backupDirectory,
                "Backup directory",
                requireAbsolute: true);
        if (PathSafety.GetRelation(backup, vault) is
        { IsEqual: true } or { IsChildOf: true } or { IsAncestorOf: true })
        {
            throw InvalidPaths(
                "Backup directory cannot overlap the Vault directory.");
        }
        if (PathSafety.GetRelation(backup, config) is
        { IsEqual: true } or { IsChildOf: true } or { IsAncestorOf: true })
        {
            throw InvalidPaths(
                "Backup directory cannot overlap the config directory.");
        }

        EnsurePathHasNoLinks(vault, "Vault directory");
        EnsurePathHasNoLinks(config, "Config directory");
        EnsurePathHasNoLinks(backup, "Backup directory");

        return new ApplicationPaths(
            vault,
            config,
            backup,
            Path.Combine(config, "data-protection"),
            Path.Combine(config, "server.lock"),
            Path.Combine(config, "mcp-token.json"),
            Path.Combine(config, "git-backup-state.json"),
            Path.Combine(backup, ".cipher-vault-backup.lock"),
            Path.Combine(config, "git-hooks-empty"),
            Path.Combine(config, "git-attributes-empty"));
    }

    public void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(VaultDirectory);
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(BackupDirectory);
            Directory.CreateDirectory(DataProtectionDirectory);
            Directory.CreateDirectory(GitHooksDirectory);
            if (!File.Exists(GitAttributesFile))
            {
                using var attributes = new FileStream(
                    GitAttributesFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
            }
            EnsurePathHasNoLinks(VaultDirectory, "Vault directory");
            EnsurePathHasNoLinks(ConfigDirectory, "Config directory");
            EnsurePathHasNoLinks(BackupDirectory, "Backup directory");
            EnsurePathHasNoLinks(GitHooksDirectory, "Git hooks directory");
            EnsurePathHasNoLinks(GitAttributesFile, "Git attributes file");
            VaultFilePermissions.ProtectDirectory(VaultDirectory);
            VaultFilePermissions.ProtectDirectory(ConfigDirectory);
            VaultFilePermissions.ProtectDirectory(BackupDirectory);
            VaultFilePermissions.ProtectDirectory(DataProtectionDirectory);
            VaultFilePermissions.ProtectDirectory(GitHooksDirectory);
            VaultFilePermissions.ProtectFile(GitAttributesFile);
        }
        catch (ServerStartupException)
        {
            throw;
        }
        catch (VaultException exception)
        {
            throw new ServerStartupException(
                ServerStartupCode.StorageFailure,
                "Unable to prepare the CipherVault directories.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new ServerStartupException(
                ServerStartupCode.StorageFailure,
                "Unable to prepare the CipherVault directories.",
                exception);
        }
    }

    private static string Canonicalize(
        string path,
        string fieldName,
        bool requireAbsolute = false)
    {
        try
        {
            return PathSafety.Canonicalize(
                path,
                fieldName,
                requireAbsolute);
        }
        catch (VaultException exception)
        {
            throw InvalidPaths(exception.Message);
        }
    }

    private static void EnsurePathHasNoLinks(
        string path,
        string fieldName)
    {
        try
        {
            PathSafety.EnsureNoLinkComponents(path);
        }
        catch (VaultException exception)
        {
            throw InvalidPaths($"{fieldName} contains an unsafe path component: {exception.Message}");
        }
    }

    private static ServerStartupException InvalidPaths(string message) =>
        new(ServerStartupCode.InvalidConfiguration, message);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
