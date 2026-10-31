using System.Security.Claims;
using CipherVault.Application.Vault;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CipherVault.Server.Security;

public static class VaultCookieAuthentication
{
    public const string Scheme = "CipherVault.Session";
    public const string CookieName = "CipherVault.Session";
    public const string SessionIdClaim = "cv:session_id";

    public static IServiceCollection AddVaultCookieAuthentication(
        this IServiceCollection services)
    {
        services
            .AddAuthentication(Scheme)
            .AddCookie(Scheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.Path = "/";
                options.Cookie.MaxAge = null;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    },
                    OnValidatePrincipal = ValidatePrincipalAsync
                };
            });
        services.AddAuthorization();
        return services;
    }

    public static Task SignInVaultSessionAsync(
        HttpContext context,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID must not be empty.", nameof(sessionId));
        }

        var identity = new ClaimsIdentity(
            [new Claim(SessionIdClaim, sessionId.ToString("D"))],
            authenticationType: Scheme);
        var principal = new ClaimsPrincipal(identity);
        return context.SignInAsync(
            Scheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false
            });
    }

    public static Task SignOutVaultSessionAsync(HttpContext context) =>
        context.SignOutAsync(Scheme);

    private static async Task ValidatePrincipalAsync(
        CookieValidatePrincipalContext context)
    {
        var claim = context.Principal?.FindFirstValue(SessionIdClaim);
        if (!Guid.TryParse(claim, out var sessionId))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
            return;
        }

        try
        {
            var manager = context.HttpContext.RequestServices
                .GetRequiredService<VaultSessionManager>();
            var status = await manager.GetStatusAsync(
                context.HttpContext.RequestAborted);
            if (status.State == VaultSessionState.Unlocked
                && status.SessionId == sessionId)
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Reject below when the request is no longer usable.
        }
        catch
        {
            // No authentication detail is exposed to the response.
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(Scheme);
    }
}
