using System.Text.Json;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Api;

public static class ApiResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = ApiJson.CreateOptions();

    public static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null,
        int? retryAfterSeconds = null)
    {
        if (retryAfterSeconds is not null)
        {
            context.Response.Headers.RetryAfter =
                retryAfterSeconds.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        var error = new ApiError(
            code,
            message,
            details ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            context.GetId());
        return WriteJsonAsync(context, statusCode, error);
    }

    public static async Task WriteJsonAsync<T>(
        HttpContext context,
        int statusCode,
        T value)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(
            value,
            JsonOptions,
            context.RequestAborted);
    }
}
