using System.Globalization;
using System.Net;
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
        IReadOnlyList<Uri>? developmentOrigins = null,
        GitBackupOptions? gitBackup = null,
        IReadOnlyList<int>? allowedHttpPorts = null)
    {
        Paths = paths;
        ListenAddresses = listenAddresses;
        IdleTimeout = idleTimeout;
        MaxRequestBodyBytes = maxRequestBodyBytes;
        ContainerMode = containerMode;
        AllowedHosts = allowedHosts ?? ["127.0.0.1", "localhost"];
        DevelopmentOrigins = developmentOrigins ?? [];
        GitBackup = gitBackup ?? GitBackupOptions.Disabled;
        AllowedHttpPorts = NormalizeAllowedHttpPorts(
            allowedHttpPorts
            ?? listenAddresses.Select(address => address.Port));
    }

    public ApplicationPaths Paths { get; }

    public IReadOnlyList<ServerListenAddress> ListenAddresses { get; }

    public TimeSpan IdleTimeout { get; }

    public long MaxRequestBodyBytes { get; }

    public bool ContainerMode { get; }

    public IReadOnlyList<string> AllowedHosts { get; }

    public IReadOnlyList<Uri> DevelopmentOrigins { get; }

    public GitBackupOptions GitBackup { get; }

    public IReadOnlyList<int> AllowedHttpPorts { get; }

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
        var allowedHttpPorts = ParseAllowedHttpPorts(
            configuration["CIPHERVAULT_PUBLIC_HTTP_PORTS"],
            addresses);
        var gitBackup = GitBackupOptions.Load(configuration, paths);
        return new ServerOptions(
            paths,
            addresses,
            idleTimeout,
            DefaultMaxRequestBodyBytes,
            containerMode,
            allowedHosts,
            developmentOrigins,
            gitBackup,
            allowedHttpPorts);
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
        foreach (var candidate in values)
        {
            if (!TryNormalizeAllowedHost(candidate, out var normalized))
            {
                throw InvalidConfiguration(
                    "CIPHERVAULT_ALLOWED_HOSTS contains a host outside the "
                    + "loopback or Tailscale ranges.");
            }
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
        }
        if (result.Count == 0)
        {
            throw InvalidConfiguration(
                "CIPHERVAULT_ALLOWED_HOSTS must contain at least one host.");
        }
        return result.AsReadOnly();
    }

    private static bool TryNormalizeAllowedHost(
        string value,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "localhost";
            return true;
        }
        if (IPAddress.TryParse(value, out var address))
        {
            if (address.Equals(IPAddress.Loopback)
                || IsTailscaleIPv4(address)
                || IsTailscaleIPv6(address))
            {
                normalized = address.ToString().ToLowerInvariant();
                return true;
            }
            return false;
        }
        if (!IsTailscaleDnsName(value))
        {
            return false;
        }
        normalized = value.ToLowerInvariant();
        return true;
    }

    private static bool IsTailscaleDnsName(string host)
    {
        if (host.Length > 253
            || !host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var labels = host.Split('.');
        if (labels.Length < 4)
        {
            return false;
        }
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63
                || label[0] == '-'
                || label[^1] == '-'
                || label.Any(character =>
                    !char.IsAsciiLetterOrDigit(character)
                    && character != '-'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsTailscaleIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily
                == System.Net.Sockets.AddressFamily.InterNetwork
            && bytes[0] == 100
            && (bytes[1] & 0xC0) == 0x40;
    }

    private static bool IsTailscaleIPv6(IPAddress address)
    {
        if (address.AddressFamily
                != System.Net.Sockets.AddressFamily.InterNetworkV6
            || address.ScopeId != 0)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 0xFD
            && bytes[1] == 0x7A
            && bytes[2] == 0x11
            && bytes[3] == 0x5C
            && bytes[4] == 0xA1
            && bytes[5] == 0xE0;
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

    private static IReadOnlyList<int> ParseAllowedHttpPorts(
        string? value,
        IReadOnlyList<ServerListenAddress> listenAddresses)
    {
        if (value is null)
        {
            return NormalizeAllowedHttpPorts(
                listenAddresses.Select(address => address.Port));
        }

        if (value.Length == 0)
        {
            throw InvalidConfiguration(
                "CIPHERVAULT_PUBLIC_HTTP_PORTS cannot be empty.");
        }

        var parts = value.Split(',');
        if (parts.Any(part => string.IsNullOrWhiteSpace(part)))
        {
            throw InvalidConfiguration(
                "CIPHERVAULT_PUBLIC_HTTP_PORTS contains an empty value.");
        }

        var ports = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(
                    part.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port)
                || port is < 1 or > 65535)
            {
                throw InvalidConfiguration(
                    "CIPHERVAULT_PUBLIC_HTTP_PORTS contains an invalid port.");
            }

            ports.Add(port);
        }

        return NormalizeAllowedHttpPorts(ports);
    }

    private static IReadOnlyList<int> NormalizeAllowedHttpPorts(
        IEnumerable<int> ports)
    {
        var normalized = ports.Distinct().Order().ToArray();
        if (normalized.Length == 0
            || normalized.Any(port => port is < 1 or > 65535))
        {
            throw new ArgumentException(
                "Allowed HTTP ports must contain valid ports.",
                nameof(ports));
        }

        return Array.AsReadOnly(normalized);
    }

    private static ServerStartupException InvalidConfiguration(string message) =>
        new(ServerStartupCode.InvalidConfiguration, message);
}
