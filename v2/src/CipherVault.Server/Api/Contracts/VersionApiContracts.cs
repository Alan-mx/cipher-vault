namespace CipherVault.Server.Api.Contracts;

public sealed record VersionApiResponse(
    string ProductVersion,
    string Commit,
    string Rid,
    string RuntimeVersion,
    string AspNetCoreVersion);
