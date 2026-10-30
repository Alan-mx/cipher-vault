namespace CipherVault.Phase0;

public sealed record ContainerEvidence(
    bool IsDocker,
    string? ContainerId,
    string Hostname,
    string? Cgroup,
    string? DeclaredImageDigest,
    DateTimeOffset DetectedAtUtc);

public sealed record PlatformInfo(
    string OsDescription,
    PlatformKind PlatformKind,
    string Rid,
    string DotnetVersion,
    string? Sqlite3McVersion,
    int? TempStoreCompileValue,
    string ProcessArchitecture,
    bool IsContainer,
    string? ContainerId);

public sealed record PackageEvidence(
    string Id,
    string Version,
    string NupkgPathRelative,
    string Sha256);

public sealed record NativeLibraryEvidence(
    string PathRelative,
    string Rid,
    long Bytes,
    string Sha256);

public sealed record EvidenceFile(
    string PathRelative,
    long Bytes,
    string Sha256,
    string Kind);

public sealed record PlaintextScanReport(
    IReadOnlyList<PlaintextScanFileResult> Files,
    bool Clean);

public sealed record PlaintextScanFileResult(
    string Target,
    long BytesScanned,
    IReadOnlyList<string> MatchedPatterns,
    string Result);
