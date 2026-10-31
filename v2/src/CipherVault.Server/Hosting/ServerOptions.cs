using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CipherVault.Server.Hosting;

public sealed record ServerListenAddress(string Host, int Port)
{
    public string Url => $"http://{Host}:{Port}";
}

public sealed record ServerOptions(
    ApplicationPaths Paths,
    IReadOnlyList<ServerListenAddress> ListenAddresses,
    TimeSpan IdleTimeout,
    long MaxRequestBodyBytes)
{
    public const long DefaultMaxRequestBodyBytes = 4L * 1024 * 1024;

    public static ServerOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var (defaultData, defaultConfig) = GetDefaultDirectories();
        var vaultDirectory = configuration["CIPHERVAULT_DATA_DIR"];
        var configDirectory = configuration["CIPHERVAULT_CONFIG_DIR"];
        var paths = ApplicationPaths.Create(
            string.IsNullOrWhiteSpace(vaultDirectory) ? defaultData : vaultDirectory,
            string.IsNullOrWhiteSpace(configDirectory) ? defaultConfig : configDirectory);

        var idleTimeout = ParseIdleTimeout(
            configuration["CIPHERVAULT_IDLE_TIMEOUT_MINUTES"]);
        var addresses = ParseListenAddresses(
            configuration["ASPNETCORE_URLS"]);
        return new ServerOptions(
            paths,
            addresses,
            idleTimeout,
            DefaultMaxRequestBodyBytes);
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

    private static IReadOnlyList<ServerListenAddress> ParseListenAddresses(
        string? value)
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
                    && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
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
