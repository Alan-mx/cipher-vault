using System.Net.Http.Json;
using System.Text.Json;

namespace CipherVault.Server.Api;

public static class ApiRequestBinding
{
    public static async Task<JsonElement> ReadRequiredJsonElementAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidRequest,
                "Request body is invalid.");
        }
    }

    public static async Task<T?> ReadOptionalJsonAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken)
        where T : class
    {
        if (request.ContentLength is null or 0
            && request.Headers.TransferEncoding.Count == 0)
        {
            return null;
        }

        try
        {
            return await request.ReadFromJsonAsync<T>(
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException
                or BadHttpRequestException)
        {
            throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidRequest,
                "Request body is invalid.");
        }
    }
}
