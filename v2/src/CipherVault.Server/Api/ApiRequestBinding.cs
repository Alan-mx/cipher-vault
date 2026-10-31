using System.Net.Http.Json;

namespace CipherVault.Server.Api;

public static class ApiRequestBinding
{
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
