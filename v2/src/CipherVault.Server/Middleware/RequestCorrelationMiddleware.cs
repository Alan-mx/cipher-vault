using System.Diagnostics;
using CipherVault.Server.Api;
using Microsoft.AspNetCore.Routing;

namespace CipherVault.Server.Middleware;

public sealed class RequestCorrelationMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<RequestCorrelationMiddleware> logger;

    public RequestCorrelationMiddleware(
        RequestDelegate next,
        ILogger<RequestCorrelationMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        context.SetId(correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            return Task.CompletedTask;
        });

        var started = Stopwatch.GetTimestamp();
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            try
            {
                await next(context);
            }
            finally
            {
                var routePattern =
                    (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                    ?? "unmatched";
                logger.LogInformation(
                    new EventId(1000, "RequestCompleted"),
                    "HTTP {Method} {RoutePattern} completed with {StatusCode} in {DurationMilliseconds}ms",
                    context.Request.Method,
                    routePattern,
                    context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }
}
