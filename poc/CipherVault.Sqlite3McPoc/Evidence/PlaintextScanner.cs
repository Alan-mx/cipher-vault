using System.Text;

namespace CipherVault.Phase0;

public sealed record ScanPattern(string Name, byte[] Bytes);

public static class PlaintextScanner
{
    private const int BufferSize = 1024 * 1024;

    public static IReadOnlyList<ScanPattern> CreatePatterns(Phase0TestData testData) =>
    [
        new("sqlitePlainHeader", Encoding.ASCII.GetBytes("SQLite format 3\0")),
        new("asciiSentinelUtf8", Encoding.UTF8.GetBytes(testData.AsciiSentinel)),
        new("asciiSentinelUtf16Le", Encoding.Unicode.GetBytes(testData.AsciiSentinel)),
        new("asciiSentinelUtf16Be", Encoding.BigEndianUnicode.GetBytes(testData.AsciiSentinel)),
        new("chineseSentinelUtf8", Encoding.UTF8.GetBytes(testData.ChineseSentinel)),
        new("chineseSentinelUtf16Le", Encoding.Unicode.GetBytes(testData.ChineseSentinel)),
        new("chineseSentinelUtf16Be", Encoding.BigEndianUnicode.GetBytes(testData.ChineseSentinel)),
        new("ftsEnglishMarkerUtf8", Encoding.UTF8.GetBytes(testData.FtsEnglishMarker)),
        new("ftsChineseMarkerUtf8", Encoding.UTF8.GetBytes(testData.FtsChineseMarker))
    ];

    public static async Task<PlaintextScanReport> ScanAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<ScanPattern> patterns,
        CancellationToken cancellationToken)
    {
        var files = roots
            .SelectMany(EnumerateFiles)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var results = new List<PlaintextScanFileResult>(files.Length);
        var relativeRoot = roots.Count == 1 ? Path.GetFullPath(roots[0]) : null;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ScanFileAsync(file, relativeRoot, patterns, cancellationToken));
        }

        return new PlaintextScanReport(results, results.All(result => result.Result == "clean"));
    }

    public static bool ContainsPattern(ReadOnlySpan<byte> content, IReadOnlyList<ScanPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (content.IndexOf(pattern.Bytes) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        if (File.Exists(root))
        {
            yield return Path.GetFullPath(root);
            yield break;
        }

        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            yield return Path.GetFullPath(file);
        }
    }

    private static async Task<PlaintextScanFileResult> ScanFileAsync(
        string path,
        string? relativeRoot,
        IReadOnlyList<ScanPattern> patterns,
        CancellationToken cancellationToken)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        var maxPattern = patterns.Count == 0 ? 0 : patterns.Max(pattern => pattern.Bytes.Length);
        var overlap = Array.Empty<byte>();
        var buffer = new byte[BufferSize];

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                total += read;
                var combined = new byte[overlap.Length + read];
                overlap.CopyTo(combined, 0);
                buffer.AsSpan(0, read).CopyTo(combined.AsSpan(overlap.Length));

                foreach (var pattern in patterns)
                {
                    if (combined.AsSpan().IndexOf(pattern.Bytes) >= 0)
                    {
                        matches.Add(pattern.Name);
                    }
                }

                var keep = Math.Min(Math.Max(0, maxPattern - 1), combined.Length);
                overlap = combined[^keep..];
                if (matches.Count == patterns.Count)
                {
                    break;
                }
            }

            return new PlaintextScanFileResult(
                relativeRoot is null
                    ? path
                    : Path.GetRelativePath(relativeRoot, path).Replace('\\', '/'),
                total,
                matches.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                matches.Count == 0 ? "clean" : "matched");
        }
        catch (IOException exception)
        {
            return new PlaintextScanFileResult(
                relativeRoot is null ? path : Path.GetRelativePath(relativeRoot, path).Replace('\\', '/'),
                total,
                [$"io:{exception.GetType().Name}"],
                "error");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new PlaintextScanFileResult(
                relativeRoot is null ? path : Path.GetRelativePath(relativeRoot, path).Replace('\\', '/'),
                total,
                [$"access:{exception.GetType().Name}"],
                "error");
        }
    }
}
