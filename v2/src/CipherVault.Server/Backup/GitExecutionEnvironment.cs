using System.Collections;
using System.Globalization;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Server.Hosting;

namespace CipherVault.Server.Backup;

internal static class GitExecutionEnvironment
{
    private static readonly HashSet<string> PreservedVariables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "PATH",
            "HOME",
            "USERPROFILE",
            "SystemRoot",
            "WINDIR",
            "TEMP",
            "TMP",
            "LOCALAPPDATA",
            "APPDATA",
            "SSH_AUTH_SOCK",
            "SSH_AGENT_PID",
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "NO_PROXY",
            "ALL_PROXY",
            "http_proxy",
            "https_proxy",
            "no_proxy",
            "all_proxy"
        };

    internal static IReadOnlyDictionary<string, string> CreateEnvironment(
        GitBackupOptions options,
        string? temporaryIndexPath = null)
    {
        ValidateConfiguration(options);
        var environment = CreateBaseEnvironment();
        var config = new List<(string Key, string Value)>
        {
            ("core.hooksPath", options.SecureHooksPath!),
            ("core.attributesFile", options.SecureAttributesPath!),
            ("core.fsmonitor", "false"),
            ("core.sshCommand", "ssh"),
            ("core.askPass", string.Empty),
            ("credential.helper", string.Empty),
            ("credential.interactive", "false"),
            ("commit.gpgSign", "false"),
            ("push.gpgSign", "false"),
            ("push.followTags", "false"),
            ("push.recurseSubmodules", "no"),
            ("push.pushOption", string.Empty),
            ("user.name", "CipherVault Backup"),
            ("user.email", "cipher-vault@localhost"),
            ("protocol.allow", "never"),
            ("protocol.https.allow", "always"),
            ("protocol.ssh.allow", "always"),
            ("protocol.ext.allow", "never")
        };
        var helper = CredentialHelperAlias(options.CredentialHelper);
        if (helper is not null)
        {
            config.Add(("credential.helper", helper));
        }

        if (options.AllowHttp)
        {
            config.Add(("protocol.http.allow", "always"));
        }

        if (options.AllowGitProtocol)
        {
            config.Add(("protocol.git.allow", "always"));
        }

        if (options.AllowFileRemote)
        {
            config.Add(("protocol.file.allow", "always"));
        }

        for (var index = 0; index < config.Count; index++)
        {
            environment[
                $"GIT_CONFIG_KEY_{index.ToString(CultureInfo.InvariantCulture)}"] =
                config[index].Key;
            environment[
                $"GIT_CONFIG_VALUE_{index.ToString(CultureInfo.InvariantCulture)}"] =
                config[index].Value;
        }

        environment["GIT_CONFIG_COUNT"] =
            config.Count.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(temporaryIndexPath))
        {
            environment["GIT_INDEX_FILE"] = PathSafety.Canonicalize(
                temporaryIndexPath,
                "Temporary Git index");
        }

        return environment;
    }

    internal static Dictionary<string, string> CreateBaseEnvironment()
    {
        var environment = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var current = Environment.GetEnvironmentVariables();
        foreach (DictionaryEntry entry in current)
        {
            if (entry.Key is not string key
                || entry.Value is not string value
                || !PreservedVariables.Contains(key))
            {
                continue;
            }

            environment[key] = value;
        }

        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "Never";
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] =
            OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        environment["GIT_ATTR_NOSYSTEM"] = "1";
        environment["GIT_PROTOCOL_FROM_USER"] = "0";
        return environment;
    }

    internal static bool IsAllowedVariable(string key) =>
        PreservedVariables.Contains(key)
        || key is "GIT_TERMINAL_PROMPT"
            or "GCM_INTERACTIVE"
            or "GIT_CONFIG_NOSYSTEM"
            or "GIT_CONFIG_GLOBAL"
            or "GIT_ATTR_NOSYSTEM"
            or "GIT_PROTOCOL_FROM_USER"
            or "GIT_INDEX_FILE"
            or "GIT_CONFIG_COUNT"
        || key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal)
        || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal);

    internal static void ValidateConfiguration(GitBackupOptions options)
    {
        if (!options.Enabled
            || options.ConfigurationErrorCode is not null
            || string.IsNullOrWhiteSpace(options.SecureHooksPath)
            || string.IsNullOrWhiteSpace(options.SecureAttributesPath))
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid);
        }

        try
        {
            var hooksPath = PathSafety.Canonicalize(
                options.SecureHooksPath,
                "Git hooks directory");
            var attributesPath = PathSafety.Canonicalize(
                options.SecureAttributesPath,
                "Git attributes file");
            if (!string.Equals(
                    hooksPath,
                    options.SecureHooksPath,
                    PathSafety.PathComparison)
                || !string.Equals(
                    attributesPath,
                    options.SecureAttributesPath,
                    PathSafety.PathComparison)
                || !Directory.Exists(hooksPath)
                || Directory.EnumerateFileSystemEntries(hooksPath).Any()
                || !PathSafety.IsRegularFile(attributesPath)
                || new FileInfo(attributesPath).Length != 0)
            {
                throw InvalidConfiguration();
            }

            PathSafety.EnsureNoLinkComponents(hooksPath);
            PathSafety.EnsureNoLinkComponents(attributesPath);
        }
        catch (GitCommandException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is VaultException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new GitCommandException(
                GitBackupErrorCodes.ConfigurationInvalid,
                exception);
        }
    }

    private static string? CredentialHelperAlias(
        GitCredentialHelperMode mode) =>
        mode switch
        {
            GitCredentialHelperMode.None => null,
            GitCredentialHelperMode.Manager => "manager",
            GitCredentialHelperMode.ManagerCore => "manager-core",
            GitCredentialHelperMode.LibSecret => "libsecret",
            GitCredentialHelperMode.Cache => "cache",
            _ => throw InvalidConfiguration()
        };

    private static GitCommandException InvalidConfiguration() =>
        new(GitBackupErrorCodes.ConfigurationInvalid);
}
