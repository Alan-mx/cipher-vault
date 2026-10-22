using CipherVault.Host.Contracts;

namespace CipherVault.Host.Client;

public sealed record AdminPipeClientOptions(
    string PipeName,
    ProcessVerificationPolicy ClientPolicy,
    ProcessVerificationPolicy ServerPolicy,
    int ConnectTimeoutMilliseconds = 5_000);
