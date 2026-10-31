using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Server.Hosting;

public sealed record ApplicationPaths(
    string VaultDirectory,
    string ConfigDirectory,
    string DataProtectionDirectory,
    string ServerLockPath,
    string McpTokenPath)
{
    public static ApplicationPaths Create(
        string vaultDirectory,
        string configDirectory)
    {
        var vault = Canonicalize(vaultDirectory);
        var config = Canonicalize(configDirectory);
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

        return new ApplicationPaths(
            vault,
            config,
            Path.Combine(config, "data-protection"),
            Path.Combine(config, "server.lock"),
            Path.Combine(config, "mcp-token.json"));
    }

    public void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(VaultDirectory);
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(DataProtectionDirectory);
            VaultFilePermissions.ProtectDirectory(VaultDirectory);
            VaultFilePermissions.ProtectDirectory(ConfigDirectory);
            VaultFilePermissions.ProtectDirectory(DataProtectionDirectory);
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

    private static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw InvalidPaths("CipherVault directory is required.");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw InvalidPaths("CipherVault directory is invalid.");
        }
    }

    private static ServerStartupException InvalidPaths(string message) =>
        new(ServerStartupCode.InvalidConfiguration, message);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
