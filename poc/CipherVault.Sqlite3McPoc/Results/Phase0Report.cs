using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace CipherVault.Phase0;

public sealed record Phase0Report(
    string Format,
    int FormatVersion,
    string RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    PlatformInfo Platform,
    IReadOnlyList<PackageEvidence> Packages,
    IReadOnlyList<NativeLibraryEvidence> NativeLibraries,
    IReadOnlyList<AcResult> Checks,
    PlatformStatus PlatformStatus,
    int ExitCode);

public sealed record Phase0GatePlatformEvidence(
    string Platform,
    string ReportPath,
    string RunId,
    PlatformStatus Status);

public sealed record Phase0GateReport(
    string Format,
    int FormatVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> ExpectedPlatforms,
    IReadOnlyList<Phase0GatePlatformEvidence> Platforms,
    IReadOnlyList<string> MissingPlatforms,
    IReadOnlyList<string> FailedPlatforms,
    PlatformStatus GateStatus);

public sealed record EvidenceIndex(
    string Format,
    IReadOnlyList<EvidenceFile> Files);

public static class Phase0Json
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

public static class Phase0ReportWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteRunAsync(
        Phase0Report report,
        ArtifactLayout layout,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(layout.ReportJsonPath, report, cancellationToken);
        await File.WriteAllTextAsync(layout.ReportTextPath, RenderText(report), Utf8NoBom, cancellationToken);
    }

    public static Task WriteScanAsync(
        PlaintextScanReport scan,
        ArtifactLayout layout,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(layout.ScanReportPath, scan, cancellationToken);

    public static Task WriteGateAsync(
        Phase0GateReport report,
        string outputPath,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(outputPath, report, cancellationToken);

    public static async Task WriteEvidenceIndexAsync(
        ArtifactLayout layout,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(layout.RunDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, layout.EvidenceIndexPath, StringComparison.Ordinal))
            .OrderBy(path => Path.GetRelativePath(layout.RunDirectory, path), StringComparer.Ordinal)
            .ToArray();
        var evidence = new List<EvidenceFile>(files.Length);
        foreach (var file in files)
        {
            evidence.Add(await ArtifactHasher.HashAsync(layout.RunDirectory, file, cancellationToken));
        }

        await WriteJsonAsync(
            layout.EvidenceIndexPath,
            new EvidenceIndex("cipher-vault-phase0-evidence-index", evidence),
            cancellationToken);
    }

    public static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, Phase0Json.Options);
        await File.WriteAllTextAsync(path, json, Utf8NoBom, cancellationToken);
    }

    private static string RenderText(Phase0Report report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Run: {report.RunId}");
        builder.AppendLine($"Platform: {report.Platform.PlatformKind}");
        builder.AppendLine($"RID: {report.Platform.Rid}");
        builder.AppendLine($"SQLite3MC: {report.Platform.Sqlite3McVersion ?? "unknown"}");
        builder.AppendLine($"Status: {JsonNamingPolicy.SnakeCaseLower.ConvertName(report.PlatformStatus.ToString()!)}");
        builder.AppendLine($"ExitCode: {report.ExitCode}");
        builder.AppendLine();

        foreach (var check in report.Checks.OrderBy(item => item.AcId, StringComparer.Ordinal))
        {
            var status = JsonNamingPolicy.SnakeCaseLower.ConvertName(check.Status.ToString()!);
            builder.AppendLine($"[{status}] {check.AcId} {check.Summary}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
            {
                builder.AppendLine($"  {check.Detail}");
            }
        }

        return builder.ToString();
    }
}
