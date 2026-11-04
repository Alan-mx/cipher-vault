using CipherVault.Application.Vault;
using CipherVault.Application.Drafts;
using CipherVault.Server.Api.Contracts;
using CipherVault.Core.Errors;
using CipherVault.Server.Security;
using Microsoft.AspNetCore.Antiforgery;

namespace CipherVault.Server.Api.Endpoints;

public static class VaultEndpoints
{
    public static IEndpointRouteBuilder MapVaultEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/vault");
        group.MapGet(
                "/status",
                async (
                    VaultLifecycleService lifecycle,
                    CancellationToken cancellationToken) =>
                {
                    var status = await lifecycle.GetStatusAsync(cancellationToken);
                    return Results.Ok(SafeVaultStatusMapper.Map(status));
                })
            .AllowAnonymous()
            .WithName("GetVaultStatus");

        group.MapPost(
                "/setup",
                async (
                    VaultSetupApiRequest request,
                    VaultLifecycleService lifecycle,
                    CancellationToken cancellationToken) =>
                {
                    var current = await lifecycle.GetStatusAsync(cancellationToken);
                    if (current.State != VaultSessionState.Uninitialized)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.VaultAlreadyInitialized,
                            "Vault is already initialized.");
                    }

                    var password = ApiInputValidation.RequirePassword(
                        request.Password,
                        "Password");
                    if (!request.RiskAccepted)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status400BadRequest,
                            ApiErrorCodes.InvalidInput,
                            "Password-loss risk acknowledgement is required.");
                    }

                    try
                    {
                        var status = await lifecycle.SetupAsync(
                            password,
                            riskAccepted: true,
                            cancellationToken);
                        return Results.Ok(SafeVaultStatusMapper.Map(status));
                    }
                    catch (VaultException exception)
                        when (exception.Code == VaultErrorCode.VaultAlreadyInitialized)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.VaultAlreadyInitialized,
                            "Vault is already initialized.");
                    }
                })
            .AllowAnonymous()
            .WithName("SetupVault");

        group.MapPost(
                "/unlock",
                async (
                    VaultUnlockApiRequest request,
                    VaultLifecycleService lifecycle,
                    UnlockAttemptLimiter limiter,
                    TimeProvider timeProvider,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var password = ApiInputValidation.RequirePassword(
                        request.Password,
                        "Password");
                    var now = timeProvider.GetUtcNow();
                    var attempt = limiter.GetStatus(now);
                    if (!attempt.CanAttempt)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status429TooManyRequests,
                            ApiErrorCodes.UnlockRateLimited,
                            "Unlock attempts are temporarily limited.",
                            new Dictionary<string, object?>
                            {
                                ["retryAfterSeconds"] =
                                    attempt.RetryAfterSeconds
                            },
                            attempt.RetryAfterSeconds);
                    }

                    VaultUnlockResult result;
                    try
                    {
                        result = await lifecycle.UnlockAsync(
                            password,
                            cancellationToken);
                    }
                    catch (VaultException exception)
                        when (exception.Code == VaultErrorCode.InvalidCredential)
                    {
                        limiter.RecordFailure(now);
                        throw;
                    }

                    limiter.RecordSuccess();
                    if (!result.CredentialAccepted
                        || result.Status.State != VaultSessionState.Unlocked
                        || result.Status.SessionId is not { } sessionId)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status409Conflict,
                            ApiErrorCodes.VaultBusy,
                            "Vault state changed during unlock.");
                    }

                    await VaultCookieAuthentication.SignInVaultSessionAsync(
                        context,
                        sessionId);
                    return Results.Ok(SafeVaultStatusMapper.Map(result.Status));
                })
            .AllowAnonymous()
            .WithName("UnlockVault");

        group.MapGet(
                "/antiforgery",
                (
                    IAntiforgery antiforgery,
                    HttpContext context) =>
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    if (string.IsNullOrEmpty(tokens.RequestToken))
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status500InternalServerError,
                            ApiErrorCodes.InternalError,
                            "Unable to issue an antiforgery token.");
                    }

                    return Results.Ok(
                        new AntiforgeryTokenResponse(tokens.RequestToken));
                })
            .RequireAuthorization()
            .WithName("GetAntiforgeryToken");

        group.MapGet(
                "/session/mcp-write-mode",
                (
                    McpWriteModeService writeMode) =>
                    Results.Ok(writeMode.Get().ToApiResponse()))
            .RequireAuthorization()
            .WithName("GetMcpWriteMode");

        group.MapPut(
                "/session/mcp-write-mode",
                async (
                    McpWriteModeApiRequest request,
                    McpWriteModeService writeMode,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var key = ApiInputValidation.RequireIdempotencyKey(
                        context.Request.Headers["Idempotency-Key"].ToString());
                    var result = await writeMode.ChangeAsync(
                        request.ToApplicationRequest(),
                        context.CreateUserActor(),
                        key,
                        cancellationToken);
                    return Results.Ok(result.ToApiResponse());
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("SetMcpWriteMode");

        group.MapPost(
                "/lock",
                async (
                    VaultLifecycleService lifecycle,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var status = await lifecycle.LockAsync(cancellationToken);
                    await VaultCookieAuthentication.SignOutVaultSessionAsync(
                        context);
                    return Results.Ok(SafeVaultStatusMapper.Map(status));
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("LockVault");

        group.MapPost(
                "/activity",
                async (
                    VaultLifecycleService lifecycle,
                    ActivityTouchLimiter limiter,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var claim = context.User.FindFirst(
                        VaultCookieAuthentication.SessionIdClaim)?.Value;
                    if (!Guid.TryParse(claim, out var sessionId))
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status401Unauthorized,
                            ApiErrorCodes.AuthenticationRequired,
                            "Authentication is required.");
                    }

                    var touch = limiter.TryAcquire(sessionId);
                    if (!touch.Allowed)
                    {
                        throw new ApiRequestException(
                            StatusCodes.Status429TooManyRequests,
                            ApiErrorCodes.ActivityRateLimited,
                            "Vault activity was refreshed too recently.",
                            new Dictionary<string, object?>
                            {
                                ["retryAfterSeconds"] =
                                    touch.RetryAfterSeconds
                            },
                            touch.RetryAfterSeconds);
                    }

                    await lifecycle.TouchActivityAsync(cancellationToken);
                    return Results.NoContent();
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("TouchVaultActivity");

        group.MapPost(
                "/change-password",
                async (
                    VaultChangePasswordApiRequest request,
                    VaultLifecycleService lifecycle,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var oldPassword = ApiInputValidation.RequirePassword(
                        request.OldPassword,
                        "OldPassword");
                    var newPassword = ApiInputValidation.RequirePassword(
                        request.NewPassword,
                        "NewPassword");
                    var status = await lifecycle.ChangePasswordAsync(
                        oldPassword,
                        newPassword,
                        cancellationToken);
                    await VaultCookieAuthentication.SignOutVaultSessionAsync(
                        context);
                    return Results.Ok(SafeVaultStatusMapper.Map(status));
                })
            .RequireAuthorization()
            .RequireAntiforgery()
            .WithName("ChangeVaultPassword");
        return endpoints;
    }
}
