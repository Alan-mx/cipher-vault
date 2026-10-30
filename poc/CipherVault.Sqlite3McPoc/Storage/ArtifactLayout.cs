namespace CipherVault.Phase0;

public sealed record ArtifactLayout(
    string ArtifactsRoot,
    string RunDirectory,
    string VaultDirectory,
    string DatabasePath,
    string TestKeyPath,
    string BackupDirectory,
    string BackupPath,
    string TamperDirectory,
    string TamperPath,
    string CrashDirectory,
    string CrashDatabasePath,
    string CrashChildLogPath,
    string TempDirectory,
    string ScanDirectory,
    string ScanReportPath,
    string DiagnosticsPath,
    string ReportJsonPath,
    string ReportTextPath,
    string ContainerEvidencePath,
    string EvidenceIndexPath)
{
    public string RunId => Path.GetFileName(RunDirectory);

    public static ArtifactLayout Create(string artifactsRoot, PlatformKind platform, DateTimeOffset now)
    {
        Directory.CreateDirectory(artifactsRoot);
        var platformName = platform.ToString().ToLowerInvariant();
        var runId = $"{platformName}-{now:yyyyMMdd-HHmmss-fff}";
        var runDirectory = Path.Combine(artifactsRoot, runId);
        while (Directory.Exists(runDirectory))
        {
            runId = $"{platformName}-{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..Math.Min(64, runId.Length + 9)];
            runDirectory = Path.Combine(artifactsRoot, runId);
        }

        var vault = Path.Combine(runDirectory, "vault");
        var backup = Path.Combine(runDirectory, "backup");
        var tamper = Path.Combine(runDirectory, "tamper");
        var crash = Path.Combine(runDirectory, "crash");
        var temp = Path.Combine(runDirectory, "temp");
        var scan = Path.Combine(runDirectory, "scan");

        foreach (var directory in new[] { runDirectory, vault, backup, tamper, crash, temp, scan })
        {
            Directory.CreateDirectory(directory);
        }

        return new ArtifactLayout(
            artifactsRoot,
            runDirectory,
            vault,
            Path.Combine(vault, "vault.db"),
            Path.Combine(vault, "test-key.bin"),
            backup,
            Path.Combine(backup, "vault.db"),
            tamper,
            Path.Combine(tamper, "vault-tamper.db"),
            crash,
            Path.Combine(crash, "vault.db"),
            Path.Combine(crash, "crash-child.log"),
            temp,
            scan,
            Path.Combine(scan, "plaintext-scan.json"),
            Path.Combine(runDirectory, "diagnostics.log"),
            Path.Combine(runDirectory, "report.json"),
            Path.Combine(runDirectory, "report.txt"),
            Path.Combine(runDirectory, "container-evidence.json"),
            Path.Combine(runDirectory, "evidence-index.json"));
    }
}
