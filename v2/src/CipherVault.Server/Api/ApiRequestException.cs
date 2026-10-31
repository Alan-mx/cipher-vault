namespace CipherVault.Server.Api;

public sealed class ApiRequestException : Exception
{
    public ApiRequestException(
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null,
        int? retryAfterSeconds = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details ??
            new Dictionary<string, object?>(StringComparer.Ordinal);
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int StatusCode { get; }

    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Details { get; }

    public int? RetryAfterSeconds { get; }
}
