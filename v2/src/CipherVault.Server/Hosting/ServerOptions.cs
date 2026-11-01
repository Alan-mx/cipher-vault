using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CipherVault.Server.Hosting;

public sealed record ServerListenAddress(string Host, int Port)
{
    public string Url => $"http://{Host}:{Port}";
}

public sealed record ServerOptions
{
    public const long DefaultMaxRequestBodyBytes = 4L * 1024 * 1024;

    public ServerOptions(
        ApplicationPaths paths,
        IReadOnlyList<ServerListenAddress> listenAddresses,
        TimeSpan idleTimeout,
        long maxRequestBodyBytes,
        bool containerMode = false,
        IReadOnlyList<string>? allowedHosts = null,
        IReadOnlyList<Uri>? developmentOrigins = null)
    {
        Paths = paths;
        ListenAddresses = listenAddresses;
        IdleTimeout = idleTimeout;
        MaxRequestBodyBytes = maxRequestBodyBytes;
        ContainerMode = containerMode;
        AllowedHosts = allowedHosts ?? ["127.0.0.1", "localhost"];
        DevelopmentOrigins = developmentOrigins ?? [];
    }

    public ApplicationPaths Paths { get; }

    public IReadOnlyList<ServerListenAddress> ListenAddresses { get; }

    public TimeSpan IdleTimeout { get; }

    public long MaxRequestBodyBytes { get; }

    public bool ContainerMode { get; }

    public IReadOnlyList<string> AllowedHosts { get; }

    public IReadOnlyList<Uri> DevelopmentOrigins { get; }

    public static ServerOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var paths = ResolvePaths(configuration, GetDefaultDirectories);
        var idleTimeout = ParseIdleTimeout(
            configuration["CIPHERVAULT_IDLE_TIMEOUT_MINUTES"]);
        var containerMode = ParseContainerMode(
            configuration["CIPHERVAULT_CONTAINER_MODE"]);
        var allowedHosts = ParseAllowedHosts(
            configuration["CIPHERVAULT_ALLOWED_HOSTS"]);
        var developmentOrigins = ParseDevelopmentOrigins(
            configuration["CIPHERVAULT_DEVELOPMENT_ORIGINS"],
            configuration["ASPNETCORE_ENVIRONMENT"]);
        var addresses = ParseListenAddresses(
            configuration["ASPNETCORE_URLS"],
            containerMode);
        return new ServerOptions(
            paths,
            addresses,
            idleTimeout,
            DefaultMaxRequestBodyBytes,
            containerMode,
            allowedHosts,
            developmentOrigins);
    }

    internal static ApplicationPaths ResolvePaths(
        IConfiguration configuration,
        Func<(string Data, string Config)> getDefaultDirectories)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(getDefaultDirectories);
        var vaultDirectory = configuration["CIPHERVAULT_DATA_DIR"];
        var configDirectory = configuration["CIPHERVAULT_CONFIG_DIR"];
        var backupDirectory = configuration["CIPHERVAULT_BACKUP_DIR"];
        var needsDefaultDirectories = string.IsNullOrWhiteSpace(vaultDirectory)
            || string.IsNullOrWhiteSpace(configDirectory);
        var (defaultData, defaultConfig) = needsDefaultDirectories
            ? getDefaultDirectories()
            : (string.Empty, string.Empty);
        return ApplicationPaths.Create(
            string.IsNullOrWhiteSpace(vaultDirectory) ? defaultData : vaultDirectory,
            string.IsNullOrWhiteSpace(configDirectory) ? defaultConfig : configDirectory,
            string.IsNullOrWhiteSpace(backupDirectory) ? null : backupDirectory);
    }

    private static (string Data, string Config) GetDefaultDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                throw InvalidConfiguration("LOCALAPPDATA is unavailable.");
            }

            var root = Path.Combine(local, "CipherVault");
            return (Path.Combine(root, "data"), Path.Combine(root, "config"));
        }

        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw InvalidConfiguration("User home directory is unavailable.");
            }

            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var dataRoot = string.IsNullOrWhiteSpace(xdgData)
                ? Path.Combine(home, ".local", "share")
                : xdgData;
            var configRoot = string.IsNullOrWhiteSpace(xdgConfig)
                ? Path.Combine(home, ".config")
                : xdgConfig;
            return (
                Path.Combine(dataRoot, "cipher-vault", "data"),
                Path.Combine(configRoot, "cipher-vault"));
        }

        throw new ServerStartupException(
            ServerStartupCode.UnsupportedPlatform,
            "CipherVault.Server supports Windows and Linux only.");
    }

    private static TimeSpan ParseIdleTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromMinutes(10);
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var minutes)
            || minutes is < 1 or > 120)
        {
            throw InvalidConfiguration(
                "CIPHERVAULT_IDLE_TIMEOUT_MINUTES must be between 1 and 120.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static bool ParseContainerMode(string? value) =>
        value switch
        {
            null or "" => false,
            "true" => true,
            "false" => false,
            _ => throw InvalidConfiguration(
                "CIPHERVAULT_CONTAINER_MODE must be true or false.")
        };

    private static IReadOnlyList<string> ParseAllowedHosts(string? value)
    {
        var values = string.IsNullOrWhiteSpace(value)
            ? ["127.0.0.1", "localhost"]
            : value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        foreach (var host in values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (host is not ("127.0.0.1" or "localhost"))
            {
                throw InvalidConfiguration(
                    "Phase 5 allows only loopback host names.");
            }
            result.Add(host);
        }
        return result;
    }

    private static IReadOnlyList<Uri> ParseDevelopmentOrigins(
        string? value,
        string? environment)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }
        if (!string.Equals(
                environment,
                "Development",
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidConfiguration(
                "CIPHERVAULT_DEVELOPMENT_ORIGINS is allowed only in Development.");
        }

        var values = value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length is < 1 or > 4)
        {
            throw InvalidConfiguration(
                "CIPHERVAULT_DEVELOPMENT_ORIGINS must contain 1 to 4 origins.");
        }

        var result = new List<Uri>();
        foreach (var candidate in values)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || uri.Port is < 1 or > 65_535
                || (uri.Host != "127.0.0.1"
                    && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                || !HasExplicitPort(candidate))
            {
                throw InvalidConfiguration(
                    "Development Origins must be exact HTTP loopback origins with a port.");
            }

            var canonical = uri.GetLeftPart(UriPartial.Authority);
            if (result.All(existing =>
                    !string.Equals(
                        existing.GetLeftPart(UriPartial.Authority),
                        canonical,
                        StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new Uri(canonical, UriKind.Absolute));
            }
        }
        return result;
    }

    private static bool HasExplicitPort(string value)
    {
        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }
        var authorityStart = schemeSeparator + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0
            ? value[authorityStart..]
            : value[authorityStart..authorityEnd];
        return authority.Contains(':', StringComparison.Ordinal);
    }

    private static IReadOnlyList<ServerListenAddress> ParseListenAddresses(
        string? value,
        bool containerMode)
    {
        var values = string.IsNullOrWhiteSpace(value)
            ? ["http://127.0.0.1:43127"]
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            throw InvalidConfiguration("ASPNETCORE_URLS must contain a listen address.");
        }

        var addresses = new List<ServerListenAddress>();
        foreach (var candidate in values)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Port is < 1 or > 65_535
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || (uri.Host != "127.0.0.1"
                    && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                    && !(containerMode && uri.Host == "0.0.0.0")))
            {
                throw InvalidConfiguration(
                    "ASPNETCORE_URLS may contain only HTTP loopback listen addresses.");
            }

            var address = new ServerListenAddress(uri.Host, uri.Port);
            if (!addresses.Contains(address))
            {
                addresses.Add(address);
            }
        }
        return addresses;
    }

    private static ServerStartupException InvalidConfiguration(string message) =>
        new(ServerStartupCode.InvalidConfiguration, message);
}
