using CipherVault.Server.Api;
using CipherVault.Server.Api.Contracts;
using CipherVault.Core.Errors;

namespace CipherVault.Server.Middleware;

public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<ApiExceptionMiddleware> logger;

    public ApiExceptionMiddleware(
        RequestDelegate next,
        ILogger<ApiExceptionMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected; no response can be delivered.
        }
        catch (Exception exception) when (context.Response.HasStarted is false)
        {
            var failure = ApiErrorMapper.Map(exception);
            var details = exception switch
            {
                VaultException vaultException => vaultException.Details,
                ApiRequestException requestException => requestException.Details,
                _ => null
            };
            var error = ApiErrorMapper.CreateError(failure, context, details);
            if (exception is ApiRequestException
                {
                    RetryAfterSeconds: { } retryAfter
                })
            {
                context.Response.Headers.RetryAfter =
                    retryAfter.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
            }
            logger.LogWarning(
                new EventId(2000, "RequestFailed"),
                "HTTP request failed with {ErrorCode} and status {StatusCode}",
                failure.Code,
                failure.StatusCode);
            await ApiResponseWriter.WriteJsonAsync(
                context,
                failure.StatusCode,
                error);
        }
    }
}
