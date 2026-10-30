using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CipherVault.Phase0;

public static class ArtifactHasher
{
    public static async Task<EvidenceFile> HashAsync(
        string artifactsRoot,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new EvidenceFile(
            Path.GetRelativePath(artifactsRoot, path).Replace('\\', '/'),
            stream.Length,
            Convert.ToHexString(hash).ToLowerInvariant(),
            InferKind(path));
    }

    public static async Task<IReadOnlyList<PackageEvidence>> CollectPackageEvidenceAsync(
        string projectAssetsPath,
        string nugetPackagesRoot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(projectAssetsPath))
        {
            throw new Phase0BlockedException($"project.assets.json not found: {projectAssetsPath}");
        }

        await using var stream = File.OpenRead(projectAssetsPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            throw new InvalidDataException("project.assets.json does not contain libraries.");
        }

        var evidence = new List<PackageEvidence>();
        foreach (var library in libraries.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package")
            {
                continue;
            }

            var separator = library.Name.LastIndexOf('/');
            if (separator <= 0 || separator == library.Name.Length - 1)
            {
                continue;
            }

            var id = library.Name[..separator];
            var version = library.Name[(separator + 1)..];
            var packageRoot = Path.Combine(nugetPackagesRoot, id.ToLowerInvariant(), version);
            var nupkgPath = Path.Combine(packageRoot, $"{id.ToLowerInvariant()}.{version}.nupkg");
            if (!File.Exists(nupkgPath))
            {
                throw new Phase0BlockedException($"NuGet package file not found: {nupkgPath}");
            }

            var hash = await HashFileAsync(nupkgPath, cancellationToken);
            evidence.Add(new PackageEvidence(
                id,
                version,
                $"{id.ToLowerInvariant()}/{version}/{Path.GetFileName(nupkgPath)}",
                hash));
        }

        return evidence
            .GroupBy(item => $"{item.Id}/{item.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task<IReadOnlyList<NativeLibraryEvidence>> CollectNativeLibraryEvidenceAsync(
        string appBaseDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(appBaseDirectory))
        {
            return [];
        }

        var currentRid = RuntimeInformation.RuntimeIdentifier;
        var files = Directory.EnumerateFiles(appBaseDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                var normalized = path.Replace('\\', '/');
                var currentRidAsset = !normalized.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains($"/runtimes/{currentRid}/", StringComparison.OrdinalIgnoreCase);
                return currentRidAsset
                    && ((name.StartsWith("sqlite3mc.", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("pclraw", StringComparison.OrdinalIgnoreCase))
                    || name.StartsWith("e_sqlite3.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("libsqlite3", StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var evidence = new List<NativeLibraryEvidence>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(file);
            evidence.Add(new NativeLibraryEvidence(
                Path.GetRelativePath(appBaseDirectory, file).Replace('\\', '/'),
                DetectRid(file),
                fileInfo.Length,
                await HashFileAsync(file, cancellationToken)));
        }

        return evidence;
    }

    private static string DetectRid(string path)
    {
        var normalized = path.Replace('\\', '/');
        var marker = "/runtimes/";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return RuntimeInformation.RuntimeIdentifier;
        }

        var rest = normalized[(index + marker.Length)..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : RuntimeInformation.RuntimeIdentifier;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string InferKind(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "json",
            ".log" or ".txt" => "text",
            ".db" => "database",
            ".bin" => "test-key",
            _ => "file"
        };
}

public sealed class Phase0BlockedException(string message) : Exception(message);
