using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;

namespace CipherVault.Server.Build;

internal static class BuildMetadataReader
{
    private const string LocalProductVersion = "0.0.0-local";
    private const string LocalCommit = "local";
    private static readonly Regex ReleaseCommit = new(
        "^[0-9a-f]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ReleaseVersion = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Lazy<BuildMetadata> Cached = new(
        ReadCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static BuildMetadata Read() => Cached.Value;

    private static BuildMetadata ReadCore()
    {
        var assembly = typeof(BuildMetadataReader).Assembly;
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Single().Value,
                StringComparer.Ordinal);
        var productVersion = ReadValue(
            metadata,
            "CipherVault.ProductVersion",
            LocalProductVersion);
        var commit = ReadValue(
            metadata,
            "CipherVault.Commit",
            LocalCommit);
        DateTimeOffset? builtAt = DateTimeOffset.TryParse(
            ReadValue(
                metadata,
                "CipherVault.BuildTimestampUtc",
                string.Empty),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
        var isLocal = string.Equals(
                productVersion,
                LocalProductVersion,
                StringComparison.Ordinal)
            && string.Equals(commit, LocalCommit, StringComparison.Ordinal);
        if (!isLocal
            && (!ReleaseVersion.IsMatch(productVersion)
                || !ReleaseCommit.IsMatch(commit)
                || builtAt is null
                || builtAt.Value.Offset != TimeSpan.Zero))
        {
            throw new InvalidOperationException(
                "Release build metadata is invalid.");
        }
        var isRelease = !isLocal;
        var aspNetCoreVersion = typeof(WebApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0];
        return new BuildMetadata(
            productVersion,
            commit,
            builtAt,
            RuntimeInformation.RuntimeIdentifier,
            Environment.Version.ToString(),
            string.IsNullOrWhiteSpace(aspNetCoreVersion)
                ? Environment.Version.ToString()
                : aspNetCoreVersion,
            isRelease);
    }

    private static string ReadValue(
        IReadOnlyDictionary<string, string?> metadata,
        string key,
        string fallback) =>
        metadata.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
}
