namespace CipherVault.Server.Build;

internal sealed record BuildMetadata(
    string ProductVersion,
    string Commit,
    DateTimeOffset? BuiltAtUtc,
    string Rid,
    string RuntimeVersion,
    string AspNetCoreVersion,
    bool IsRelease);
