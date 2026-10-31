using CipherVault.Server.Api;
using Microsoft.AspNetCore.Antiforgery;

namespace CipherVault.Server.Security;

public static class AntiforgeryConfiguration
{
    public const string CookieName = "CipherVault.Antiforgery";
    public const string HeaderName = "X-CSRF-TOKEN";

    public static IServiceCollection AddVaultAntiforgery(
        this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            options.FormFieldName = "__CipherVaultAntiforgery";
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Path = "/";
        });
        return services;
    }

    public static RouteHandlerBuilder RequireAntiforgery(
        this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<AntiforgeryEndpointFilter>();
}

public sealed class AntiforgeryEndpointFilter : IEndpointFilter
{
    private readonly IAntiforgery antiforgery;

    public AntiforgeryEndpointFilter(IAntiforgery antiforgery)
    {
        this.antiforgery = antiforgery;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.AntiforgeryInvalid,
                "Antiforgery validation failed.");
        }

        return await next(context);
    }
}
