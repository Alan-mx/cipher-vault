namespace CipherVault.Phase0;

public enum Phase0Command
{
    Run,
    CrashChild,
    LifecycleChild,
    Gate
}

public enum PlatformKind
{
    Windows,
    Linux,
    Docker
}

public sealed record Phase0Options(
    Phase0Command Command,
    string ArtifactsRoot,
    PlatformKind Platform,
    string? PlainSqliteProbePath,
    string? RunDirectory)
{
    public static Phase0Options Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new Phase0UsageException("A command is required.");
        }

        return args[0] switch
        {
            "run" => ParseRun(args[1..]),
            "crash-child" => ParseCrashChild(args[1..]),
            "lifecycle-child" => ParseLifecycleChild(args[1..]),
            "gate" => ParseGate(args[1..]),
            _ => throw new Phase0UsageException($"Unknown command '{args[0]}'.")
        };
    }

    private static Phase0Options ParseRun(string[] args)
    {
        var values = ParseFlags(args);
        var artifactsRoot = GetValue(values, "--artifacts-root")
            ?? Path.Combine(Environment.CurrentDirectory, "poc", "artifacts", "phase0");
        var platform = ParsePlatform(GetValue(values, "--platform") ?? "auto");
        var plainProbe = GetValue(values, "--plain-sqlite-probe")
            ?? throw new Phase0UsageException("--plain-sqlite-probe is required for run.");

        EnsureKnownFlags(values, "--artifacts-root", "--platform", "--plain-sqlite-probe");
        return new Phase0Options(
            Phase0Command.Run,
            Path.GetFullPath(artifactsRoot),
            platform,
            Path.GetFullPath(plainProbe),
            null);
    }

    private static Phase0Options ParseCrashChild(string[] args)
    {
        var values = ParseFlags(args);
        var runDirectory = GetValue(values, "--run-dir")
            ?? throw new Phase0UsageException("--run-dir is required for crash-child.");

        EnsureKnownFlags(values, "--run-dir");
        return new Phase0Options(
            Phase0Command.CrashChild,
            Path.GetFullPath(runDirectory),
            OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Linux,
            null,
            Path.GetFullPath(runDirectory));
    }

    private static Phase0Options ParseLifecycleChild(string[] args)
    {
        var values = ParseFlags(args);
        var runDirectory = GetValue(values, "--run-dir")
            ?? throw new Phase0UsageException("--run-dir is required for lifecycle-child.");

        EnsureKnownFlags(values, "--run-dir");
        return new Phase0Options(
            Phase0Command.LifecycleChild,
            Path.GetFullPath(runDirectory),
            OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Linux,
            null,
            Path.GetFullPath(runDirectory));
    }

    private static Phase0Options ParseGate(string[] args)
    {
        var values = ParseFlags(args);
        var artifactsRoot = GetValue(values, "--artifacts-root")
            ?? Path.Combine(Environment.CurrentDirectory, "poc", "artifacts", "phase0");

        EnsureKnownFlags(values, "--artifacts-root");
        return new Phase0Options(
            Phase0Command.Gate,
            Path.GetFullPath(artifactsRoot),
            OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Linux,
            null,
            null);
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (!flag.StartsWith("--", StringComparison.Ordinal))
            {
                throw new Phase0UsageException($"Unexpected argument '{flag}'.");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new Phase0UsageException($"Missing value for '{flag}'.");
            }

            if (!values.TryAdd(flag, args[++index]))
            {
                throw new Phase0UsageException($"Duplicate flag '{flag}'.");
            }
        }

        return values;
    }

    private static string? GetValue(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static void EnsureKnownFlags(Dictionary<string, string> values, params string[] knownFlags)
    {
        var known = new HashSet<string>(knownFlags, StringComparer.Ordinal);
        foreach (var key in values.Keys)
        {
            if (!known.Contains(key))
            {
                throw new Phase0UsageException($"Unknown flag '{key}'.");
            }
        }
    }

    private static PlatformKind ParsePlatform(string value) =>
        value.ToLowerInvariant() switch
        {
            "auto" => OperatingSystem.IsWindows() ? PlatformKind.Windows : PlatformKind.Linux,
            "windows" => PlatformKind.Windows,
            "linux" => PlatformKind.Linux,
            "docker" => PlatformKind.Docker,
            _ => throw new Phase0UsageException($"Unknown platform '{value}'.")
        };
}

public sealed class Phase0UsageException(string message) : Exception(message);
