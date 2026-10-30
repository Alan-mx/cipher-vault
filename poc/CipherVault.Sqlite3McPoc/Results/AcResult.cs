namespace CipherVault.Phase0;

public sealed record AcResult(
    string AcId,
    AcStatus Status,
    string Summary,
    string? Detail = null,
    string? EvidencePath = null);

public sealed record SqliteReadResult(
    bool IntegrityCheckPassed,
    bool AsciiSentinelMatched,
    bool ChineseSentinelMatched,
    long FtsEnglishCount,
    long FtsChineseCount,
    string? FailureKind,
    string? ErrorSummary);

public sealed record WrongKeyProbeResult(
    bool Rejected,
    string? ExceptionType,
    int? SqliteErrorCode,
    string? ErrorSummary);

public sealed record PlainSqliteProbeResult(
    bool Started,
    int ExitCode,
    bool Rejected,
    string? StdoutSummary,
    string? StderrSummary);

public sealed record TamperProbeResult(
    bool Rejected,
    long TamperOffset,
    string? ExceptionType,
    string? ErrorSummary);

public sealed record DatabaseHeaderEvidence(
    string Bytes0To23Hex,
    string First100Sha256,
    string FirstPageSha256,
    long FileBytes,
    long PageSize,
    long PageCount,
    long SchemaVersion,
    long ApplicationId,
    long UserVersion,
    bool ContainsSentinel);

public sealed record CrashProbeResult(
    bool ChildStarted,
    bool ReadyReceived,
    bool ForcedKill,
    bool ReopenAttempted,
    bool IntegrityCheckPassed,
    bool BaselinePresent,
    bool CrashMarkerAbsent,
    bool JournalResidueDetected,
    bool PlaintextClean,
    bool Passed);

public sealed record SqliteRuntimeInfo(
    string Sqlite3McVersion,
    int? TempStoreCompileValue,
    int? TempStoreRuntimeValue,
    string? NativeLibraryPath);

public sealed record Phase0TestData(
    string AsciiSentinel,
    string ChineseSentinel,
    string FtsEnglishMarker,
    string FtsChineseMarker);
