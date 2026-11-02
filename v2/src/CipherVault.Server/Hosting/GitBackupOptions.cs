using System.Globalization;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Extensions.Configuration;

namespace CipherVault.Server.Hosting;

public enum GitCredentialHelperMode
{
    None,
    Manager,
    ManagerCore,
    LibSecret,
    Cache
}

public sealed record GitBackupOptions(
    bool Enabled,
    string? RepositoryPath,
    string Remote,
    string Branch,
    TimeSpan DebounceDelay,
    TimeSpan MaxDelay,
    TimeSpan RetryDelay,
    long RotationBytes,
    TimeSpan RotationAge,
    string? ConfigurationErrorCode,
    GitCredentialHelperMode CredentialHelper,
    bool AllowHttp,
    bool AllowGitProtocol,
    bool AllowFileRemote,
    string? SecureHooksPath,
    string? SecureAttributesPath)
{
    public const string InvalidConfigurationCode = "git_config_invalid";
    public const long DefaultRotationBytes = 1024L * 1024 * 1024;

    public static GitBackupOptions Disabled { get; } = new(
        Enabled: false,
        RepositoryPath: null,
        Remote: "origin",
        Branch: "main",
        DebounceDelay: TimeSpan.FromMinutes(5),
        MaxDelay: TimeSpan.FromMinutes(30),
        RetryDelay: TimeSpan.FromSeconds(60),
        RotationBytes: DefaultRotationBytes,
        RotationAge: TimeSpan.FromDays(365),
        ConfigurationErrorCode: null,
        CredentialHelper: DefaultCredentialHelper,
        AllowHttp: false,
        AllowGitProtocol: false,
        AllowFileRemote: false,
        SecureHooksPath: null,
        SecureAttributesPath: null);

    public bool IsUsable => Enabled && ConfigurationErrorCode is null;

    public static GitBackupOptions Load(
        IConfiguration configuration,
        ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        bool enabled;
        try
        {
            enabled = ParseBoolean(
                configuration["CIPHERVAULT_GIT_BACKUP_ENABLED"],
                defaultValue: false);
        }
        catch (FormatException)
        {
            return Disabled with
            {
                Enabled = true,
                ConfigurationErrorCode = InvalidConfigurationCode
            };
        }

        if (!enabled)
        {
            return Disabled;
        }

        try
        {
            var repositoryPath = PathSafety.Canonicalize(
                configuration["CIPHERVAULT_GIT_BACKUP_REPO"]
                    ?? throw Invalid("Repository path is required."),
                "Git backup repository");
            if (!Directory.Exists(repositoryPath))
            {
                throw Invalid("Git backup repository does not exist.");
            }

            PathSafety.EnsureNoLinkComponents(repositoryPath);
            EnsureDisjoint(repositoryPath, paths);

            var remote = ParseName(
                configuration["CIPHERVAULT_GIT_BACKUP_REMOTE"],
                "origin",
                "Remote",
                maxLength: 128);
            var branch = ParseName(
                configuration["CIPHERVAULT_GIT_BACKUP_BRANCH"],
                "main",
                "Branch",
                maxLength: 255);
            var debounce = TimeSpan.FromSeconds(ParseInteger(
                configuration["CIPHERVAULT_GIT_BACKUP_DEBOUNCE_SECONDS"],
                300,
                60,
                3600,
                "CIPHERVAULT_GIT_BACKUP_DEBOUNCE_SECONDS"));
            var maxDelay = TimeSpan.FromSeconds(ParseInteger(
                configuration["CIPHERVAULT_GIT_BACKUP_MAX_DELAY_SECONDS"],
                1800,
                300,
                86400,
                "CIPHERVAULT_GIT_BACKUP_MAX_DELAY_SECONDS"));
            if (maxDelay < debounce)
            {
                throw Invalid("Maximum delay cannot be shorter than debounce delay.");
            }

            var retryDelay = TimeSpan.FromSeconds(ParseInteger(
                configuration["CIPHERVAULT_GIT_BACKUP_RETRY_SECONDS"],
                60,
                30,
                3600,
                "CIPHERVAULT_GIT_BACKUP_RETRY_SECONDS"));
            var rotationBytes = ParseLong(
                configuration["CIPHERVAULT_GIT_BACKUP_ROTATE_BYTES"],
                DefaultRotationBytes,
                100L * 1024 * 1024,
                long.MaxValue,
                "CIPHERVAULT_GIT_BACKUP_ROTATE_BYTES");
            var rotationDays = ParseInteger(
                configuration["CIPHERVAULT_GIT_BACKUP_ROTATE_DAYS"],
                365,
                30,
                3650,
                "CIPHERVAULT_GIT_BACKUP_ROTATE_DAYS");
            var credentialHelper = ParseCredentialHelper(
                configuration["CIPHERVAULT_GIT_CREDENTIAL_HELPER"]);
            var allowHttp = ParseBoolean(
                configuration["CIPHERVAULT_GIT_ALLOW_HTTP"],
                defaultValue: false);
            var allowGit = ParseBoolean(
                configuration["CIPHERVAULT_GIT_ALLOW_GIT_PROTOCOL"],
                defaultValue: false);
            var allowFile = ParseBoolean(
                configuration["CIPHERVAULT_GIT_ALLOW_FILE_REMOTE"],
                defaultValue: false);

            return new GitBackupOptions(
                Enabled: true,
                RepositoryPath: repositoryPath,
                Remote: remote,
                Branch: branch,
                DebounceDelay: debounce,
                MaxDelay: maxDelay,
                RetryDelay: retryDelay,
                RotationBytes: rotationBytes,
                RotationAge: TimeSpan.FromDays(rotationDays),
                ConfigurationErrorCode: null,
                CredentialHelper: credentialHelper,
                AllowHttp: allowHttp,
                AllowGitProtocol: allowGit,
                AllowFileRemote: allowFile,
                SecureHooksPath: paths.GitHooksDirectory,
                SecureAttributesPath: paths.GitAttributesFile);
        }
        catch (Exception exception) when (
            exception is VaultException
                or FormatException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return Disabled with
            {
                Enabled = true,
                ConfigurationErrorCode = InvalidConfigurationCode
            };
        }
    }

    private static void EnsureDisjoint(
        string repositoryPath,
        ApplicationPaths paths)
    {
        foreach (var (otherPath, name) in new[]
        {
            (paths.VaultDirectory, "Vault directory"),
            (paths.ConfigDirectory, "config directory"),
            (paths.BackupDirectory, "backup directory")
        })
        {
            if (PathSafety.GetRelation(repositoryPath, otherPath) is
            { IsEqual: true } or { IsChildOf: true } or { IsAncestorOf: true })
            {
                throw Invalid(
                    $"Git backup repository cannot overlap the {name}.");
            }
        }
    }

    private static bool ParseBoolean(string? value, bool defaultValue) =>
        value switch
        {
            null or "" => defaultValue,
            "true" => true,
            "false" => false,
            _ => throw new FormatException()
        };

    private static string ParseName(
        string? value,
        string defaultValue,
        string fieldName,
        int maxLength)
    {
        var result = string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : value.Trim();
        var valid = fieldName == "Remote"
            ? IsValidRemoteName(result, maxLength)
            : IsValidBranchName(result, maxLength);
        if (!valid)
        {
            throw Invalid($"{fieldName} is invalid.");
        }

        return result;
    }

    private static bool IsValidRemoteName(string value, int maxLength) =>
        value.Length is >= 1 and <= 128
        && value.Length <= maxLength
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-')
        && !value.Contains("..", StringComparison.Ordinal);

    private static bool IsValidBranchName(string value, int maxLength) =>
        value.Length is >= 1 and <= 255
        && value.Length <= maxLength
        && value[0] is not ('-' or '.' or '/')
        && value[^1] is not ('.' or '/')
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal)
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && value != "@"
        && value.All(character =>
            !char.IsWhiteSpace(character)
            && !char.IsControl(character)
            && character is not ('~' or '^' or ':' or '?' or '*'
                or '[' or '\\' or '}' or '+'));

    private static GitCredentialHelperMode ParseCredentialHelper(
        string? value) =>
        value switch
        {
            null or "" => DefaultCredentialHelper,
            "none" => GitCredentialHelperMode.None,
            "manager" => GitCredentialHelperMode.Manager,
            "manager-core" => GitCredentialHelperMode.ManagerCore,
            "libsecret" => GitCredentialHelperMode.LibSecret,
            "cache" => GitCredentialHelperMode.Cache,
            _ => throw Invalid("Credential helper is invalid.")
        };

    private static GitCredentialHelperMode DefaultCredentialHelper =>
        OperatingSystem.IsWindows()
            ? GitCredentialHelperMode.Manager
            : GitCredentialHelperMode.None;

    private static int ParseInteger(
        string? value,
        int defaultValue,
        int minimum,
        int maximum,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result)
            || result < minimum
            || result > maximum)
        {
            throw Invalid(
                $"{fieldName} must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static long ParseLong(
        string? value,
        long defaultValue,
        long minimum,
        long maximum,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result)
            || result < minimum
            || result > maximum)
        {
            throw Invalid(
                $"{fieldName} must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static FormatException Invalid(string message) =>
        new(message);
}
