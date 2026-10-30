using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CipherVault.Phase0;

namespace CipherVault.Phase0;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        Phase0Cli.RunAsync(args, CancellationToken.None);
}

public static class Phase0Cli
{
    private const string Usage = """
        CipherVault Phase 0 SQLite3MC PoC

        Usage:
          run --plain-sqlite-probe <dll> [--artifacts-root <path>] [--platform auto|windows|linux|docker]
          crash-child --run-dir <path>
          lifecycle-child --run-dir <path>
          gate [--artifacts-root <path>]

        Exit codes:
          0 passed
          1 failed
          2 blocked
          3 usage error
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        Phase0Options options;
        try
        {
            options = Phase0Options.Parse(args);
        }
        catch (Phase0UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Usage);
            return 3;
        }

        try
        {
            return options.Command switch
            {
                Phase0Command.Run => await RunCommandAsync(options, cancellationToken),
                Phase0Command.CrashChild => await Phase0Runner.RunCrashChildAsync(options, cancellationToken),
                Phase0Command.LifecycleChild => await Phase0Runner.RunLifecycleChildAsync(options, cancellationToken),
                Phase0Command.Gate => await Phase0GateEvaluator.RunAsync(options, cancellationToken),
                _ => 3
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Phase 0 operation was cancelled.");
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(Phase0Options options, CancellationToken cancellationToken)
    {
        var report = await Phase0Runner.RunPlatformAsync(options, cancellationToken);
        return report.ExitCode;
    }
}

public static partial class Phase0Runner
{
    private const string ExpectedDockerDigest =
        "sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2";

    public static async Task<Phase0Report> RunPlatformAsync(
        Phase0Options options,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var layout = ArtifactLayout.Create(options.ArtifactsRoot, options.Platform, startedAt);
        Environment.SetEnvironmentVariable("SQLITE_TMPDIR", layout.TempDirectory);
        var checks = CreateInitialChecks(options.Platform);
        var diagnostics = new List<string>();
        var packages = new List<PackageEvidence>();
        var nativeLibraries = new List<NativeLibraryEvidence>();
        SqliteRuntimeInfo? runtime = null;
        TestKeyMaterial? keyMaterial = null;
        var testData = CreateTestData();
        var scanPatterns = new List<ScanPattern>();
        CrashProbeResult? crashProbe = null;
        var container = DetectContainer();
        var platformMatches = PlatformMatches(options.Platform, container);

        try
        {
            await File.WriteAllTextAsync(
                layout.DiagnosticsPath,
                $"Run {layout.RunId}{Environment.NewLine}",
                new UTF8Encoding(false),
                cancellationToken);

            if (!platformMatches)
            {
                diagnostics.Add("Requested platform does not match the current runtime/container environment.");
                Set(
                    checks,
                    "AC23",
                    options.Platform == PlatformKind.Windows ? AcStatus.Blocked : AcStatus.NotApplicable,
                    "Windows platform evidence");
                Set(
                    checks,
                    "AC24",
                    options.Platform == PlatformKind.Linux ? AcStatus.Blocked : AcStatus.NotApplicable,
                    "Linux platform evidence");
                Set(
                    checks,
                    "AC25",
                    options.Platform == PlatformKind.Docker ? AcStatus.Blocked : AcStatus.NotApplicable,
                    "Docker platform evidence");
            }
            else
            {
                runtime = await SqliteMcTestDatabase.InitializeProcessAsync(cancellationToken);
                diagnostics.Add($"SQLite3MC initialized: {runtime.Sqlite3McVersion}");
                var projectRoot = FindProjectRoot(AppContext.BaseDirectory, "CipherVault.Sqlite3McPoc");
                var projectAssets = Path.Combine(projectRoot, "obj", "project.assets.json");
                var probeFile = new FileInfo(options.PlainSqliteProbePath!);
                var probeDirectory = probeFile.DirectoryName
                    ?? throw new DirectoryNotFoundException("Plain SQLite probe directory was not found.");
                var probeProjectRoot = FindProjectRoot(probeDirectory, "CipherVault.PlainSqliteProbe");
                var probeProjectAssets = Path.Combine(probeProjectRoot, "obj", "project.assets.json");
                var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
                packages.AddRange(await ArtifactHasher.CollectPackageEvidenceAsync(
                    projectAssets,
                    nugetRoot,
                    cancellationToken));
                packages.AddRange(await ArtifactHasher.CollectPackageEvidenceAsync(
                    probeProjectAssets,
                    nugetRoot,
                    cancellationToken));
                nativeLibraries.AddRange(await ArtifactHasher.CollectNativeLibraryEvidenceAsync(
                    AppContext.BaseDirectory,
                    cancellationToken));
                nativeLibraries.AddRange(await ArtifactHasher.CollectNativeLibraryEvidenceAsync(
                    probeDirectory,
                    cancellationToken));

                var lockFile = Path.Combine(projectRoot, "packages.lock.json");
                var probeLockFile = Path.Combine(probeProjectRoot, "packages.lock.json");
                var mainAssetsText = await File.ReadAllTextAsync(projectAssets, cancellationToken);
                var probeAssetsText = await File.ReadAllTextAsync(probeProjectAssets, cancellationToken);
                var requiredPackage = packages.Any(item =>
                    item.Id.Equals("SQLite3MC.PCLRaw.bundle", StringComparison.OrdinalIgnoreCase)
                    && item.Version == "2.6.1");
                var requiredProbePackage = packages.Any(item =>
                    item.Id.Equals("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase)
                    && item.Version == "10.0.14");
                Set(
                    checks,
                    "AC1",
                    File.Exists(lockFile) && File.Exists(projectAssets)
                        && File.Exists(probeLockFile) && File.Exists(probeProjectAssets)
                        ? AcStatus.Pass
                        : AcStatus.Fail,
                    "Locked dependency files",
                    $"mainLock={File.Exists(lockFile)} mainAssets={File.Exists(projectAssets)} probeLock={File.Exists(probeLockFile)} probeAssets={File.Exists(probeProjectAssets)}");
                Set(
                    checks,
                    "AC2",
                    requiredPackage
                        && requiredProbePackage
                        && !mainAssetsText.Contains("SQLitePCLRaw.bundle_e_sqlcipher", StringComparison.OrdinalIgnoreCase)
                        && probeAssetsText.Contains("SQLitePCLRaw.bundle_e_sqlite3", StringComparison.OrdinalIgnoreCase)
                        ? AcStatus.Pass
                        : AcStatus.Fail,
                    "Separate SQLite bundles per process",
                    $"sqlite3mc={requiredPackage}; plainProbe={requiredProbePackage}");
                Set(
                    checks,
                    "AC3",
                    string.IsNullOrWhiteSpace(runtime.Sqlite3McVersion) ? AcStatus.Fail : AcStatus.Pass,
                    "Runtime evidence",
                    $"version={runtime.Sqlite3McVersion}; native={runtime.NativeLibraryPath ?? "unknown"}");

                keyMaterial = await TestKeyMaterial.CreateAsync(layout.TestKeyPath, cancellationToken);
                await SqliteMcTestDatabase.CreateAndSeedAsync(
                    layout.DatabasePath,
                    keyMaterial.DatabaseKey,
                    testData,
                    cancellationToken);
                Set(checks, "AC4", AcStatus.Pass, "Encrypted database created and seeded");

                var readable = await SqliteMcTestDatabase.VerifyReadableAsync(
                    layout.DatabasePath,
                    keyMaterial.DatabaseKey,
                    testData,
                    cancellationToken);
                var readablePass = readable.IntegrityCheckPassed
                    && readable.AsciiSentinelMatched
                    && readable.ChineseSentinelMatched
                    && readable.FtsEnglishCount > 0
                    && readable.FtsChineseCount > 0;
                Set(
                    checks,
                    "AC5",
                    readablePass ? AcStatus.Pass : AcStatus.Fail,
                    "Database, sentinels and FTS5 readable",
                    readable.ErrorSummary);

                var wrongKey = await TestKeyMaterial.CreateAsync(
                    Path.Combine(layout.VaultDirectory, "wrong-key.bin"),
                    cancellationToken);
                try
                {
                    var wrongKeyResult = await SqliteMcTestDatabase.VerifyWrongKeyRejectedAsync(
                        layout.DatabasePath,
                        wrongKey.DatabaseKey,
                        cancellationToken);
                    Set(
                        checks,
                        "AC6",
                        wrongKeyResult.Rejected ? AcStatus.Pass : AcStatus.Fail,
                        "Wrong DatabaseKey rejected",
                        wrongKeyResult.ErrorSummary);
                }
                finally
                {
                    wrongKey.Clear();
                    File.Delete(Path.Combine(layout.VaultDirectory, "wrong-key.bin"));
                }

                var plainProbe = await RunPlainSqliteProbeAsync(
                    options.PlainSqliteProbePath!,
                    layout.DatabasePath,
                    cancellationToken);
                Set(
                    checks,
                    "AC7",
                    plainProbe.Started && plainProbe.Rejected ? AcStatus.Pass : AcStatus.Fail,
                    "Plain SQLite rejected encrypted database",
                    plainProbe.StderrSummary ?? plainProbe.StdoutSummary);

                var tamper = await SqliteMcTestDatabase.VerifyTamperRejectedAsync(
                    layout.DatabasePath,
                    layout.TamperPath,
                    keyMaterial.DatabaseKey,
                    cancellationToken);
                Set(
                    checks,
                    "AC8",
                    tamper.Rejected ? AcStatus.Pass : AcStatus.Fail,
                    "Ciphertext tamper rejected",
                    tamper.ErrorSummary);

                await SqliteMcTestDatabase.RunTempPressureAsync(
                    layout.DatabasePath,
                    keyMaterial.DatabaseKey,
                    cancellationToken);
                Set(checks, "AC10", AcStatus.Pass, "Every connection forced temp_store=MEMORY");

                if (runtime.TempStoreCompileValue is null)
                {
                    Set(checks, "AC11", AcStatus.Blocked, "SQLITE_TEMP_STORE compile value unavailable");
                    Set(checks, "AC14", AcStatus.Blocked, "SQLITE_TEMP_STORE evidence unavailable");
                }
                else
                {
                    var compilePass = runtime.TempStoreCompileValue is 2 or 3;
                    Set(
                        checks,
                        "AC11",
                        compilePass ? AcStatus.Pass : AcStatus.Fail,
                        "SQLITE_TEMP_STORE compile value",
                        $"value={runtime.TempStoreCompileValue}");
                    Set(
                        checks,
                        "AC14",
                        AcStatus.Pass,
                        "SQLITE_TEMP_STORE runtime evidence available",
                        $"value={runtime.TempStoreCompileValue}");
                }

                var headerEvidence = await SqliteMcTestDatabase.ReadHeaderEvidenceAsync(
                    layout.DatabasePath,
                    keyMaterial.DatabaseKey,
                    testData,
                    cancellationToken);
                var headerEvidencePath = Path.Combine(layout.ScanDirectory, "database-header.json");
                await Phase0ReportWriter.WriteJsonAsync(headerEvidencePath, headerEvidence, cancellationToken);
                Set(
                    checks,
                    "AC13",
                    !headerEvidence.ContainsSentinel
                        ? AcStatus.Pass
                        : AcStatus.Fail,
                    "Database header metadata recorded",
                    $"bytes0To23={headerEvidence.Bytes0To23Hex}; pageSize={headerEvidence.PageSize}; pageCount={headerEvidence.PageCount}");

                await SqliteMcTestDatabase.CreateEncryptedBackupAsync(
                    layout.DatabasePath,
                    layout.BackupPath,
                    keyMaterial.DatabaseKey,
                    cancellationToken);
                Set(checks, "AC15", AcStatus.Pass, "Encrypted SQLite backup snapshot created");

                var backupReadable = await SqliteMcTestDatabase.VerifyBackupAsync(
                    layout.BackupPath,
                    keyMaterial.DatabaseKey,
                    testData,
                    cancellationToken);
                var backupPass = backupReadable.IntegrityCheckPassed
                    && backupReadable.AsciiSentinelMatched
                    && backupReadable.ChineseSentinelMatched;
                Set(
                    checks,
                    "AC16",
                    backupPass ? AcStatus.Pass : AcStatus.Fail,
                    "Encrypted backup reopened",
                    backupReadable.ErrorSummary);

                var backupWrongKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                var backupWrongKeyResult = await SqliteMcTestDatabase.VerifyWrongKeyRejectedAsync(
                    layout.BackupPath,
                    backupWrongKey,
                    cancellationToken);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(backupWrongKey);
                var backupTamperPath = Path.Combine(layout.TamperDirectory, "backup-tamper.db");
                var backupTamper = await SqliteMcTestDatabase.VerifyTamperRejectedAsync(
                    layout.BackupPath,
                    backupTamperPath,
                    keyMaterial.DatabaseKey,
                    cancellationToken);
                var backupRejectionPass = backupWrongKeyResult.Rejected && backupTamper.Rejected;
                Set(
                    checks,
                    "AC17",
                    backupRejectionPass ? AcStatus.Pass : AcStatus.Fail,
                    "Wrong key and tampered backup rejected",
                    backupWrongKeyResult.ErrorSummary ?? backupTamper.ErrorSummary);
                Set(checks, "AC19", AcStatus.Pass, "Backup artifact is explicitly test-only");

                File.Copy(layout.DatabasePath, layout.CrashDatabasePath, overwrite: true);
                crashProbe = await RunCrashProbeAsync(layout, keyMaterial, cancellationToken);
                var lifecyclePassed = await RunLifecycleProbeAsync(layout, cancellationToken);
                Set(
                    checks,
                    "AC21",
                    lifecyclePassed ? AcStatus.Pass : AcStatus.Fail,
                    "Fresh SQLite3MC processes initialize successfully");

                scanPatterns.AddRange(PlaintextScanner.CreatePatterns(testData));
                scanPatterns.Add(new ScanPattern(
                    "rawKeyPrefix",
                    Encoding.ASCII.GetBytes("raw:")));
                scanPatterns.Add(new ScanPattern(
                    "rawKeyHex",
                    Encoding.ASCII.GetBytes(Convert.ToHexString(keyMaterial.DatabaseKey))));
                scanPatterns.Add(new ScanPattern(
                    "rawKeyHexLower",
                    Encoding.ASCII.GetBytes(Convert.ToHexString(keyMaterial.DatabaseKey).ToLowerInvariant())));
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Core probe failed: {Sanitize(exception.ToString())}");
            SetFirstBlockedCommonFailure(checks, "AC4", AcStatus.Fail, "Core probe exception", Sanitize(exception.Message));
        }
        finally
        {
            keyMaterial?.Clear();
        }

        var containerEvidence = container;
        await Phase0ReportWriter.WriteJsonAsync(layout.ContainerEvidencePath, containerEvidence, cancellationToken);
        packages = packages
            .GroupBy(item => $"{item.Id}/{item.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ToList();
        nativeLibraries = nativeLibraries
            .GroupBy(item => $"{item.Rid}/{item.PathRelative}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Rid, StringComparer.Ordinal)
            .ThenBy(item => item.PathRelative, StringComparer.Ordinal)
            .ToList();

        if (runtime is not null)
        {
            var version = await Task.Run(SqliteMcTestDatabase.ShutdownProcess, cancellationToken);
            diagnostics.Add($"sqlite3_shutdown result: {version}");
            Set(
                checks,
                "AC20",
                version == 0 ? AcStatus.Pass : AcStatus.Fail,
                "sqlite3_shutdown completed",
                $"result={version}");
        }

        await File.AppendAllLinesAsync(layout.DiagnosticsPath, diagnostics, new UTF8Encoding(false), cancellationToken);
        var platformInfo = BuildPlatformInfo(options.Platform, runtime, container);
        var provisionalReport = BuildReport(layout, startedAt, platformInfo, packages, nativeLibraries, checks);
        await Phase0ReportWriter.WriteRunAsync(provisionalReport, layout, cancellationToken);

        if (scanPatterns.Count > 0)
        {
            var scan = await PlaintextScanner.ScanAsync(
                [layout.RunDirectory],
                scanPatterns,
                cancellationToken);
            var matchedPatterns = scan.Files
                .SelectMany(file => file.MatchedPatterns)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var rawKeyLeak = matchedPatterns.Any(pattern =>
                pattern is "rawKeyPrefix" or "rawKeyHex");
            Set(
                checks,
                "AC09",
                rawKeyLeak ? AcStatus.Fail : AcStatus.Pass,
                "Raw DatabaseKey absent from artifacts",
                rawKeyLeak ? string.Join(", ", matchedPatterns) : null);
            Set(
                checks,
                "AC12",
                scan.Clean ? AcStatus.Pass : AcStatus.Fail,
                "TEMP and artifact plaintext scan",
                scan.Clean ? null : string.Join(", ", matchedPatterns));
            Set(
                checks,
                "AC18",
                scan.Clean ? AcStatus.Pass : AcStatus.Fail,
                "Database, backup and temp plaintext scan",
                scan.Clean ? null : string.Join(", ", matchedPatterns));
            var crashPassed = crashProbe?.Passed == true && scan.Clean;
            Set(
                checks,
                "AC22",
                crashPassed ? AcStatus.Pass : AcStatus.Fail,
                "Forced process termination recovered consistently",
                crashProbe is null
                    ? "Crash probe did not execute."
                    : $"integrity={crashProbe.IntegrityCheckPassed}; baseline={crashProbe.BaselinePresent}; markerAbsent={crashProbe.CrashMarkerAbsent}; journalResidue={crashProbe.JournalResidueDetected}");
            await Phase0ReportWriter.WriteScanAsync(scan, layout, cancellationToken);
        }

        FinalizePlatformChecks(
            checks,
            options.Platform,
            platformMatches,
            container,
            packages,
            nativeLibraries,
            File.Exists(layout.ScanReportPath));
        var report = BuildReport(layout, startedAt, platformInfo, packages, nativeLibraries, checks);
        await Phase0ReportWriter.WriteRunAsync(report, layout, cancellationToken);
        var finalReportBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, Phase0Json.Options));
        if (PlaintextScanner.ContainsPattern(finalReportBytes, scanPatterns))
        {
            Set(checks, "AC09", AcStatus.Fail, "Final report contains raw key material");
            Set(checks, "AC12", AcStatus.Fail, "Final report contains a plaintext sentinel");
            report = BuildReport(layout, startedAt, platformInfo, packages, nativeLibraries, checks);
            await Phase0ReportWriter.WriteRunAsync(report, layout, cancellationToken);
        }

        await Phase0ReportWriter.WriteEvidenceIndexAsync(layout, cancellationToken);
        return report;
    }

    public static async Task<int> RunCrashChildAsync(
        Phase0Options options,
        CancellationToken cancellationToken)
    {
        TestKeyMaterial? key = null;
        var initialized = false;
        try
        {
            _ = await SqliteMcTestDatabase.InitializeProcessAsync(cancellationToken);
            initialized = true;
            key = await TestKeyMaterial.LoadAsync(
                Path.Combine(options.RunDirectory!, "vault", "test-key.bin"),
                cancellationToken);
            await SqliteMcTestDatabase.WriteUncommittedCrashMarkerAndWaitAsync(
                Path.Combine(options.RunDirectory!, "crash", "vault.db"),
                key.DatabaseKey,
                cancellationToken);
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(Sanitize(exception.Message));
            return 1;
        }
        finally
        {
            key?.Clear();
            if (initialized)
            {
                _ = SqliteMcTestDatabase.ShutdownProcess();
            }
        }
    }

    public static async Task<int> RunLifecycleChildAsync(
        Phase0Options options,
        CancellationToken cancellationToken)
    {
        TestKeyMaterial? key = null;
        var initialized = false;
        try
        {
            _ = await SqliteMcTestDatabase.InitializeProcessAsync(cancellationToken);
            initialized = true;
            key = await TestKeyMaterial.LoadAsync(
                Path.Combine(options.RunDirectory!, "vault", "test-key.bin"),
                cancellationToken);
            var readable = await SqliteMcTestDatabase.VerifyReadableAsync(
                Path.Combine(options.RunDirectory!, "vault", "vault.db"),
                key.DatabaseKey,
                CreateTestData(),
                cancellationToken);
            if (!readable.IntegrityCheckPassed)
            {
                Console.Error.WriteLine("Lifecycle child integrity check failed.");
                return 1;
            }

            Console.Out.WriteLine("CV_PHASE0_LIFECYCLE_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(Sanitize(exception.Message));
            return 1;
        }
        finally
        {
            key?.Clear();
            if (initialized)
            {
                _ = SqliteMcTestDatabase.ShutdownProcess();
            }
        }
    }

    private static List<AcResult> CreateInitialChecks(PlatformKind platform)
    {
        var checks = Enumerable.Range(1, 27)
            .Select(index => new AcResult(
                $"AC{index:D2}",
                AcStatus.Blocked,
                "Not executed."))
            .ToList();

        Set(checks, "AC23", platform == PlatformKind.Windows ? AcStatus.Blocked : AcStatus.NotApplicable, "Windows platform evidence");
        Set(checks, "AC24", platform == PlatformKind.Linux ? AcStatus.Blocked : AcStatus.NotApplicable, "Linux platform evidence");
        Set(checks, "AC25", platform == PlatformKind.Docker ? AcStatus.Blocked : AcStatus.NotApplicable, "Docker platform evidence");
        Set(checks, "AC26", AcStatus.NotApplicable, "Evaluated by Gate");
        return checks;
    }

    private static void SetFirstBlockedCommonFailure(
        List<AcResult> checks,
        string startAc,
        AcStatus status,
        string summary,
        string? detail)
    {
        var start = int.Parse(startAc[2..], System.Globalization.CultureInfo.InvariantCulture);
        for (var index = start; index <= 22; index++)
        {
            var id = $"AC{index:D2}";
            if (checks.First(item => item.AcId == id).Status == AcStatus.Blocked)
            {
                Set(checks, id, status, summary, detail);
                return;
            }
        }
    }

    private static void FinalizePlatformChecks(
        List<AcResult> checks,
        PlatformKind requestedPlatform,
        bool platformMatches,
        ContainerEvidence container,
        IReadOnlyList<PackageEvidence> packages,
        IReadOnlyList<NativeLibraryEvidence> nativeLibraries,
        bool scanWritten)
    {
        var common = checks
            .Where(item =>
            {
                var number = int.Parse(item.AcId[2..], System.Globalization.CultureInfo.InvariantCulture);
                return number is >= 1 and <= 22;
            })
            .ToArray();
        var commonStatus = common.Any(item => item.Status == AcStatus.Fail)
            ? AcStatus.Fail
            : common.Any(item => item.Status == AcStatus.Blocked)
                ? AcStatus.Blocked
                : AcStatus.Pass;

        var platformAc = requestedPlatform switch
        {
            PlatformKind.Windows => "AC23",
            PlatformKind.Linux => "AC24",
            PlatformKind.Docker => "AC25",
            _ => throw new ArgumentOutOfRangeException(nameof(requestedPlatform))
        };
        var platformSummary = requestedPlatform switch
        {
            PlatformKind.Windows => "Windows platform evidence",
            PlatformKind.Linux => "Linux platform evidence",
            PlatformKind.Docker => "Docker platform evidence",
            _ => "Platform evidence"
        };

        if (requestedPlatform == PlatformKind.Docker && platformMatches)
        {
            if (string.IsNullOrWhiteSpace(container.DeclaredImageDigest))
            {
                Set(checks, platformAc, AcStatus.Blocked, platformSummary, "CV_PHASE0_IMAGE_DIGEST is missing.");
            }
            else if (!string.Equals(container.DeclaredImageDigest, ExpectedDockerDigest, StringComparison.OrdinalIgnoreCase))
            {
                Set(checks, platformAc, AcStatus.Fail, platformSummary, "Container image digest mismatch.");
            }
            else
            {
                Set(checks, platformAc, commonStatus, platformSummary, $"image={ExpectedDockerDigest}");
            }
        }
        else
        {
            Set(checks, platformAc, platformMatches ? commonStatus : AcStatus.Blocked, platformSummary);
        }

        Set(
            checks,
            "AC27",
            scanWritten && packages.Count > 0 && nativeLibraries.Count > 0
                ? AcStatus.Pass
                : AcStatus.Blocked,
            "Report and evidence completeness",
            $"packages={packages.Count}; nativeLibraries={nativeLibraries.Count}; scan={scanWritten}");
    }

    private static void Set(
        List<AcResult> checks,
        string acId,
        AcStatus status,
        string summary,
        string? detail = null)
    {
        var normalizedId = acId.Length > 2
            && acId.StartsWith("AC", StringComparison.Ordinal)
            && int.TryParse(acId[2..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? $"AC{number:D2}"
                : acId;
        var index = checks.FindIndex(item => item.AcId == normalizedId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Unknown AC id '{acId}'.");
        }

        checks[index] = new AcResult(normalizedId, status, summary, detail);
    }

    private static PlatformInfo BuildPlatformInfo(
        PlatformKind platform,
        SqliteRuntimeInfo? runtime,
        ContainerEvidence container) =>
        new(
            RuntimeInformation.OSDescription,
            platform,
            RuntimeInformation.RuntimeIdentifier,
            Environment.Version.ToString(),
            runtime?.Sqlite3McVersion,
            runtime?.TempStoreCompileValue,
            RuntimeInformation.ProcessArchitecture.ToString(),
            container.IsDocker,
            container.ContainerId);

    private static Phase0Report BuildReport(
        ArtifactLayout layout,
        DateTimeOffset startedAt,
        PlatformInfo platform,
        IReadOnlyList<PackageEvidence> packages,
        IReadOnlyList<NativeLibraryEvidence> nativeLibraries,
        IReadOnlyList<AcResult> checks)
    {
        var status = checks.Any(item => item.Status == AcStatus.Fail)
            ? PlatformStatus.Failed
            : checks.Any(item => item.Status == AcStatus.Blocked)
                ? PlatformStatus.Blocked
                : PlatformStatus.Passed;
        var exitCode = status switch
        {
            PlatformStatus.Passed => 0,
            PlatformStatus.Failed => 1,
            _ => 2
        };

        return new Phase0Report(
            "cipher-vault-phase0-report",
            1,
            layout.RunId,
            startedAt,
            DateTimeOffset.UtcNow,
            platform,
            packages,
            nativeLibraries,
            checks.OrderBy(item => item.AcId, StringComparer.Ordinal).ToArray(),
            status,
            exitCode);
    }

    private static async Task<PlainSqliteProbeResult> RunPlainSqliteProbeAsync(
        string probeAssemblyPath,
        string dbPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(probeAssemblyPath))
        {
            return new PlainSqliteProbeResult(false, -1, false, null, "Plain SQLite probe assembly not found.");
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(probeAssemblyPath);
        startInfo.ArgumentList.Add(dbPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new PlainSqliteProbeResult(false, -1, false, null, "Failed to start plain SQLite probe.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var jsonLine = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (jsonLine is null)
        {
            return new PlainSqliteProbeResult(true, process.ExitCode, false, null, Truncate(stderr));
        }

        using var document = JsonDocument.Parse(jsonLine);
        var rejected = document.RootElement.TryGetProperty("rejected", out var rejectedElement)
            && rejectedElement.ValueKind == JsonValueKind.True;
        return new PlainSqliteProbeResult(
            true,
            process.ExitCode,
            rejected,
            Truncate(jsonLine),
            Truncate(stderr));
    }

    private static async Task<CrashProbeResult> RunCrashProbeAsync(
        ArtifactLayout layout,
        TestKeyMaterial key,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateChildStartInfo(
            "crash-child",
            "--run-dir",
            layout.RunDirectory);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new CrashProbeResult(
                false, false, false, false, false, false, false, false, false, false);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = new StringBuilder();
        var readyReceived = false;
        var forcedKill = false;
        try
        {
            var readyLine = await process.StandardOutput
                .ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (readyLine is not null)
            {
                stdout.AppendLine(readyLine);
                readyReceived = string.Equals(
                    readyLine.Trim(),
                    "CV_PHASE0_CRASH_READY",
                    StringComparison.Ordinal);
            }
        }
        catch (TimeoutException)
        {
            stdout.AppendLine("Crash child ready timeout.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                forcedKill = true;
            }

            await process.WaitForExitAsync(cancellationToken);
        }

        stdout.Append(await process.StandardOutput.ReadToEndAsync(cancellationToken));
        var stderr = await stderrTask;
        await File.WriteAllTextAsync(
            layout.CrashChildLogPath,
            stdout.ToString() + Environment.NewLine + stderr,
            new UTF8Encoding(false),
            cancellationToken);

        if (!readyReceived || !forcedKill)
        {
            return new CrashProbeResult(
                true, readyReceived, forcedKill, false, false, false, false, false, false, false);
        }

        try
        {
            var readable = await SqliteMcTestDatabase.VerifyReadableAsync(
                layout.CrashDatabasePath,
                key.DatabaseKey,
                CreateTestData(),
                cancellationToken);
            var marker = await SqliteMcTestDatabase.ReadCrashMarkerAsync(
                layout.CrashDatabasePath,
                key.DatabaseKey,
                cancellationToken);
            await SqliteMcTestDatabase.CompleteCrashRecoveryAsync(
                layout.CrashDatabasePath,
                key.DatabaseKey,
                cancellationToken);
            var journalResidue = FindSqliteSidecars(layout.CrashDatabasePath).Length > 0;
            var baselinePresent = string.Equals(marker, "BASELINE", StringComparison.Ordinal);
            var markerAbsent = !string.Equals(marker, "CV_CRASH_UNCOMMITTED", StringComparison.Ordinal);
            var passed = readable.IntegrityCheckPassed
                && baselinePresent
                && markerAbsent
                && !journalResidue;
            return new CrashProbeResult(
                true,
                readyReceived,
                forcedKill,
                true,
                readable.IntegrityCheckPassed,
                baselinePresent,
                markerAbsent,
                journalResidue,
                true,
                passed);
        }
        catch (Exception exception)
        {
            await File.AppendAllTextAsync(
                layout.CrashChildLogPath,
                $"{Environment.NewLine}reopen failure: {Sanitize(exception.Message)}",
                new UTF8Encoding(false),
                cancellationToken);
            return new CrashProbeResult(
                true, readyReceived, forcedKill, true, false, false, false, false, false, false);
        }
    }

    private static async Task<bool> RunLifecycleProbeAsync(
        ArtifactLayout layout,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var startInfo = CreateChildStartInfo(
                "lifecycle-child",
                "--run-dir",
                layout.RunDirectory);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(cancellationToken);
                return false;
            }

            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0
                || !stdout.Contains("CV_PHASE0_LIFECYCLE_OK", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ProcessStartInfo CreateChildStartInfo(params string[] arguments)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(Phase0Runner).Assembly.Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string[] FindSqliteSidecars(string dbPath)
    {
        var candidates = new[]
        {
            dbPath + "-journal",
            dbPath + "-wal",
            dbPath + "-shm"
        };
        return candidates.Where(File.Exists).ToArray();
    }

    private static Phase0TestData CreateTestData() =>
        new(
            "CV_PHASE0_SECRET_6D2A94F1C8E7",
            "睡眠质量明显下降，半夜经常醒来。",
            "phasezeroftsmarker",
            "睡眠质量");

    private static string FindProjectRoot(string startPath, string projectName)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, $"{projectName}.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Project root for {projectName} was not found.");
    }

    private static ContainerEvidence DetectContainer()
    {
        var detectedAt = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsLinux())
        {
            return new ContainerEvidence(
                false,
                null,
                Environment.MachineName,
                null,
                Environment.GetEnvironmentVariable("CV_PHASE0_IMAGE_DIGEST"),
                detectedAt);
        }

        var cgroupPath = "/proc/self/cgroup";
        var cgroup = File.Exists(cgroupPath) ? File.ReadAllText(cgroupPath).Trim() : null;
        var isDocker = File.Exists("/.dockerenv")
            || (cgroup?.Contains("docker", StringComparison.OrdinalIgnoreCase) ?? false)
            || (cgroup?.Contains("containerd", StringComparison.OrdinalIgnoreCase) ?? false);
        var containerId = ExtractContainerId(cgroup) ?? (isDocker ? Environment.MachineName : null);
        return new ContainerEvidence(
            isDocker,
            containerId,
            Environment.MachineName,
            cgroup,
            Environment.GetEnvironmentVariable("CV_PHASE0_IMAGE_DIGEST"),
            detectedAt);
    }

    private static bool PlatformMatches(PlatformKind requested, ContainerEvidence container) =>
        requested switch
        {
            PlatformKind.Windows => OperatingSystem.IsWindows() && !container.IsDocker,
            PlatformKind.Linux => OperatingSystem.IsLinux() && !container.IsDocker,
            PlatformKind.Docker => OperatingSystem.IsLinux() && container.IsDocker,
            _ => false
        };

    private static string? ExtractContainerId(string? cgroup)
    {
        if (string.IsNullOrWhiteSpace(cgroup))
        {
            return null;
        }

        var token = cgroup
            .Split(['/', '\n', ':'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(part => part.Length >= 12 && part.All(Uri.IsHexDigit));
        return token is null ? null : token[..Math.Min(64, token.Length)];
    }

    private static string Truncate(string value) =>
        value.Length <= 2048 ? value : value[..2048];

    private static string Sanitize(string message) =>
        message.Replace("raw:", "raw:<redacted>", StringComparison.OrdinalIgnoreCase)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}

public static class Phase0GateEvaluator
{
    private const string ExpectedDockerDigest =
        "sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2";

    private static readonly string[] ExpectedPlatforms = ["windows", "linux", "docker"];

    public static async Task<int> RunAsync(Phase0Options options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ArtifactsRoot);
        var platformEvidence = new List<Phase0GatePlatformEvidence>();
        var missing = new List<string>();
        var failed = new List<string>();

        foreach (var platform in ExpectedPlatforms)
        {
            var latest = FindLatestReport(options.ArtifactsRoot, platform);
            if (latest is null)
            {
                missing.Add(platform);
                continue;
            }

            try
            {
                var report = await LoadAndValidateReportAsync(latest, platform, cancellationToken);
                platformEvidence.Add(new Phase0GatePlatformEvidence(
                    platform,
                    Path.GetRelativePath(options.ArtifactsRoot, latest).Replace('\\', '/'),
                    report.RunId,
                    report.PlatformStatus));
                if (report.PlatformStatus != PlatformStatus.Passed)
                {
                    failed.Add(platform);
                }
            }
            catch (Exception exception)
            {
                failed.Add(platform);
                platformEvidence.Add(new Phase0GatePlatformEvidence(
                    platform,
                    Path.GetRelativePath(options.ArtifactsRoot, latest).Replace('\\', '/'),
                    Path.GetFileName(Path.GetDirectoryName(latest)!) ?? platform,
                    PlatformStatus.Failed));
                await File.AppendAllTextAsync(
                    Path.Combine(Path.GetDirectoryName(latest)!, "gate-validation-error.log"),
                    Sanitize(exception.Message) + Environment.NewLine,
                    new UTF8Encoding(false),
                    cancellationToken);
            }
        }

        var gateStatus = failed.Count > 0
            ? PlatformStatus.Failed
            : missing.Count > 0
                ? PlatformStatus.Blocked
                : PlatformStatus.Passed;
        var gateReport = new Phase0GateReport(
            "cipher-vault-phase0-gate",
            1,
            DateTimeOffset.UtcNow,
            ExpectedPlatforms,
            platformEvidence.OrderBy(item => item.Platform, StringComparer.Ordinal).ToArray(),
            missing,
            failed,
            gateStatus);
        var outputPath = Path.Combine(options.ArtifactsRoot, "phase0-gate.json");
        await Phase0ReportWriter.WriteGateAsync(gateReport, outputPath, cancellationToken);
        Console.WriteLine($"Phase 0 Gate: {JsonNamingPolicy.SnakeCaseLower.ConvertName(gateStatus.ToString()!)}");
        Console.WriteLine($"Gate report: {outputPath}");
        return gateStatus switch
        {
            PlatformStatus.Passed => 0,
            PlatformStatus.Failed => 1,
            _ => 2
        };
    }

    private static string? FindLatestReport(string artifactsRoot, string platform)
    {
        if (!Directory.Exists(artifactsRoot))
        {
            return null;
        }

        return Directory.EnumerateFiles(artifactsRoot, "report.json", SearchOption.AllDirectories)
            .Where(path =>
            {
                var runName = Path.GetFileName(Path.GetDirectoryName(path));
                return runName?.StartsWith($"{platform}-", StringComparison.Ordinal) == true;
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static async Task<Phase0Report> LoadAndValidateReportAsync(
        string reportPath,
        string expectedPlatform,
        CancellationToken cancellationToken)
    {
        var runDirectory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidDataException("Report has no run directory.");
        await using var stream = File.OpenRead(reportPath);
        var report = await JsonSerializer.DeserializeAsync<Phase0Report>(
            stream,
            Phase0Json.Options,
            cancellationToken) ?? throw new InvalidDataException("Report JSON was empty.");

        Require(report.Format == "cipher-vault-phase0-report", "Invalid report format.");
        Require(report.FormatVersion == 1, "Invalid report version.");
        Require(report.RunId.StartsWith($"{expectedPlatform}-", StringComparison.Ordinal), "RunId platform mismatch.");
        Require(
            report.Platform.PlatformKind.ToString().ToLowerInvariant() == expectedPlatform,
            "PlatformKind mismatch.");
        Require(report.Checks.Count == 27, "Report must contain exactly 27 AC entries.");
        Require(report.Checks.Select(item => item.AcId).Distinct(StringComparer.Ordinal).Count() == 27, "Duplicate AC ids.");

        var ordered = report.Checks.OrderBy(item => item.AcId, StringComparer.Ordinal).ToArray();
        Require(
            ordered.Select(item => item.AcId).SequenceEqual(Enumerable.Range(1, 27).Select(index => $"AC{index:D2}")),
            "AC ids are incomplete.");
        for (var number = 1; number <= 22; number++)
        {
            Require(GetCheck(ordered, number).Status != AcStatus.NotApplicable, $"AC{number:D2} cannot be not_applicable.");
        }

        Require(GetCheck(ordered, 26).Status == AcStatus.NotApplicable, "AC26 must be evaluated only by Gate.");
        Require(GetCheck(ordered, 27).Status == AcStatus.Pass, "AC27 must pass for a valid report.");
        foreach (var number in new[] { 23, 24, 25 })
        {
            var check = GetCheck(ordered, number);
            var isCurrentPlatform =
                (expectedPlatform == "windows" && number == 23)
                || (expectedPlatform == "linux" && number == 24)
                || (expectedPlatform == "docker" && number == 25);
            if (!isCurrentPlatform)
            {
                Require(check.Status == AcStatus.NotApplicable, $"AC{number:D2} must be not_applicable.");
            }
        }

        var computedStatus = report.Checks.Any(item => item.Status == AcStatus.Fail)
            ? PlatformStatus.Failed
            : report.Checks.Any(item => item.Status == AcStatus.Blocked)
                ? PlatformStatus.Blocked
                : PlatformStatus.Passed;
        Require(report.PlatformStatus == computedStatus, "Platform status is inconsistent with AC results.");
        var expectedExitCode = computedStatus switch
        {
            PlatformStatus.Passed => 0,
            PlatformStatus.Failed => 1,
            _ => 2
        };
        Require(report.ExitCode == expectedExitCode, "Exit code is inconsistent with platform status.");

        if (expectedPlatform == "docker")
        {
            Require(report.Platform.IsContainer && !string.IsNullOrWhiteSpace(report.Platform.ContainerId), "Docker evidence missing.");
            var containerPath = Path.Combine(runDirectory, "container-evidence.json");
            Require(File.Exists(containerPath), "container-evidence.json is missing.");
            await using var containerStream = File.OpenRead(containerPath);
            var container = await JsonSerializer.DeserializeAsync<ContainerEvidence>(
                containerStream,
                Phase0Json.Options,
                cancellationToken) ?? throw new InvalidDataException("Container evidence is empty.");
            Require(container.IsDocker, "Docker runtime was not detected.");
            Require(
                string.Equals(container.DeclaredImageDigest, ExpectedDockerDigest, StringComparison.OrdinalIgnoreCase),
                "Docker image digest mismatch.");
        }
        else
        {
            Require(!report.Platform.IsContainer, "Host platform report unexpectedly came from a container.");
        }

        await ValidateEvidenceIndexAsync(runDirectory, cancellationToken);
        return report;
    }

    private static async Task ValidateEvidenceIndexAsync(
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(runDirectory, "evidence-index.json");
        Require(File.Exists(indexPath), "evidence-index.json is missing.");
        await using var stream = File.OpenRead(indexPath);
        var index = await JsonSerializer.DeserializeAsync<EvidenceIndex>(
            stream,
            Phase0Json.Options,
            cancellationToken) ?? throw new InvalidDataException("Evidence index is empty.");
        Require(index.Format == "cipher-vault-phase0-evidence-index", "Invalid evidence index format.");
        Require(index.Files.Count > 0, "Evidence index is empty.");

        var root = Path.GetFullPath(runDirectory) + Path.DirectorySeparatorChar;
        foreach (var evidence in index.Files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(runDirectory, evidence.PathRelative));
            Require(fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Evidence path escapes run directory.");
            Require(File.Exists(fullPath), $"Evidence file is missing: {evidence.PathRelative}");
            var actual = await ArtifactHasher.HashAsync(runDirectory, fullPath, cancellationToken);
            Require(actual.Bytes == evidence.Bytes, $"Evidence size mismatch: {evidence.PathRelative}");
            Require(
                string.Equals(actual.Sha256, evidence.Sha256, StringComparison.Ordinal),
                $"Evidence hash mismatch: {evidence.PathRelative}");
        }
    }

    private static AcResult GetCheck(IReadOnlyList<AcResult> checks, int number) =>
        checks.First(item => item.AcId == $"AC{number:D2}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string Sanitize(string message) =>
        message.Replace("raw:", "raw:<redacted>", StringComparison.OrdinalIgnoreCase)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
