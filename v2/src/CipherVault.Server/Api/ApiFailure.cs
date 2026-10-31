namespace CipherVault.Server.Api;

public sealed record ApiFailure(int StatusCode, string Code, string Message);
