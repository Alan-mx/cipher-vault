using CipherVault.Server.Api;
using CipherVault.Server.Hosting;
using Microsoft.AspNetCore.Http.Features;

namespace CipherVault.Server.Middleware;

public sealed class RequestBodyLimitMiddleware
{
    private readonly RequestDelegate next;
    private readonly ServerOptions options;

    public RequestBodyLimitMiddleware(
        RequestDelegate next,
        ServerOptions options)
    {
        this.next = next;
        this.options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not null && !feature.IsReadOnly)
        {
            feature.MaxRequestBodySize = options.MaxRequestBodyBytes;
        }

        if (context.Request.ContentLength > options.MaxRequestBodyBytes)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.RequestTooLarge,
                "Request body is too large.");
            return;
        }

        await next(context);
    }
}
