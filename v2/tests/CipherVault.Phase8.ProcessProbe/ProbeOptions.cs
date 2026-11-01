namespace CipherVault.Phase8.ProcessProbe;

internal static class ProbeEnvironment
{
    public static Dictionary<string, string> Create(
        string data,
        string config,
        string backup,
        string baseUrl) =>
        new(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = baseUrl,
            ["CIPHERVAULT_DATA_DIR"] = Path.GetFullPath(data),
            ["CIPHERVAULT_CONFIG_DIR"] = Path.GetFullPath(config),
            ["CIPHERVAULT_BACKUP_DIR"] = Path.GetFullPath(backup),
            ["CIPHERVAULT_CONTAINER_MODE"] = "false"
        };
}

internal sealed class ProbeOptions
{
    private ProbeOptions()
    {
    }

    public string Command { get; private init; } = string.Empty;

    public string Server { get; private init; } = string.Empty;

    public string DataDirectory { get; private init; } = string.Empty;

    public string ConfigDirectory { get; private init; } = string.Empty;

    public string BackupDirectory { get; private init; } = string.Empty;

    public string BaseUrl { get; private init; } = string.Empty;

    public string Package { get; private init; } = string.Empty;

    public string Target { get; private init; } = string.Empty;

    public string PasswordPipe { get; private init; } = string.Empty;

    public string? ReadyFile { get; private init; }

    public string? ContinueFile { get; private init; }

    public string? EvidenceFile { get; private init; }

    public TimeSpan Timeout { get; private init; }

    public static ProbeOptions Parse(string[] args)
    {
        if (args.Length < 2
            || args[0] is not ("run-server" or "restore"))
        {
            throw new ProbeException("phase8_probe_usage");
        }
        var values = new Dictionary<string, string>(
            StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length
                || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ProbeException("phase8_probe_arguments_invalid");
            }
            var key = args[index][2..];
            if (!values.TryAdd(key, args[index + 1]))
            {
                throw new ProbeException("phase8_probe_arguments_invalid");
            }
        }

        var port = ReadInt(values, "port", 43127);
        var timeoutSeconds = ReadInt(
            values,
            "shutdown-timeout-seconds",
            30);
        var host = Read(values, "host", "127.0.0.1");
        if (port is < 1 or > 65_535
            || timeoutSeconds is < 1 or > 300
            || host is not ("127.0.0.1" or "localhost"))
        {
            throw new ProbeException("phase8_probe_arguments_invalid");
        }

        return new ProbeOptions
        {
            Command = args[0],
            Server = FullPath(ReadRequired(values, "server")),
            DataDirectory = FullPath(ReadRequired(values, "data")),
            ConfigDirectory = FullPath(ReadRequired(values, "config")),
            BackupDirectory = FullPath(ReadRequired(values, "backup")),
            BaseUrl = $"http://{host}:{port}",
            Package = FullPath(Read(values, "package", string.Empty)),
            Target = FullPath(Read(values, "target", string.Empty)),
            PasswordPipe = Read(values, "password-pipe", string.Empty),
            ReadyFile = OptionalFullPath(values, "ready-file"),
            ContinueFile = OptionalFullPath(values, "continue-file"),
            EvidenceFile = OptionalFullPath(values, "evidence"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    private static string ReadRequired(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var value = ReadOptional(values, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProbeException("phase8_probe_arguments_invalid");
        }
        return value;
    }

    private static string? ReadOptional(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string? OptionalFullPath(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var value = ReadOptional(values, key);
        return string.IsNullOrWhiteSpace(value) ? null : FullPath(value);
    }

    private static string FullPath(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value);

    private static string Read(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback) =>
        values.TryGetValue(key, out var value)
            && int.TryParse(value, out var parsed)
                ? parsed
                : fallback;
}
