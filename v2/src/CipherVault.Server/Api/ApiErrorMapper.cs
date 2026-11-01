using CipherVault.Core.Errors;
using CipherVault.Server.Api.Contracts;
using Microsoft.AspNetCore.Antiforgery;

namespace CipherVault.Server.Api;

public static class ApiErrorMapper
{
    private static readonly HashSet<string> SafeDetailKeys =
    [
        "currentRevision",
        "candidateHeadingPaths",
        "documentId",
        "revisionId",
        "errorCode",
        "importItemIndex",
        "limit",
        "maximum",
        "actualBytes",
        "purgeEligibleAt",
        "retryAfterSeconds"
    ];

    public static ApiFailure Map(Exception exception) =>
        exception switch
        {
            ApiRequestException request => new(
                request.StatusCode,
                request.Code,
                request.Message),
            AntiforgeryValidationException => new(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.AntiforgeryInvalid,
                "Antiforgery validation failed."),
            BadHttpRequestException badRequest
                when badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge => new(
                    StatusCodes.Status413PayloadTooLarge,
                    ApiErrorCodes.RequestTooLarge,
                    "Request body is too large."),
            BadHttpRequestException => new(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidRequest,
                "Request is invalid."),
            VaultException vaultException => MapVaultException(vaultException),
            _ => new(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The request failed.")
        };

    public static ApiError CreateError(
        ApiFailure failure,
        HttpContext context,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        return new ApiError(
            failure.Code,
            failure.Message,
            Sanitize(details),
            context.GetId());
    }

    public static ApiError CreateVaultError(
        VaultException exception,
        HttpContext context)
    {
        var failure = MapVaultException(exception);
        return CreateError(failure, context, exception.Details);
    }

    private static ApiFailure MapVaultException(VaultException exception) =>
        exception.Code switch
        {
            VaultErrorCode.InvalidInput => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidInput,
                "Request validation failed."),
            VaultErrorCode.InvalidCursor => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidCursor,
                "Cursor is invalid for this query."),
            VaultErrorCode.FrontMatterInvalid => Failure(
                StatusCodes.Status400BadRequest,
                "frontmatter_invalid",
                "Markdown Frontmatter is invalid."),
            VaultErrorCode.TagLimitExceeded => Failure(
                StatusCodes.Status400BadRequest,
                "tag_limit_exceeded",
                "Document tags exceed the allowed limit."),
            VaultErrorCode.ImportPreviewInvalid => Failure(
                StatusCodes.Status400BadRequest,
                "import_preview_invalid",
                "Import preview is invalid or expired."),
            VaultErrorCode.ImportSourceInvalid => Failure(
                StatusCodes.Status400BadRequest,
                "import_source_invalid",
                "Import source is invalid."),
            VaultErrorCode.InvalidCredential => Failure(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.InvalidCredential,
                "Vault credential is invalid."),
            VaultErrorCode.Forbidden => Failure(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                "This operation is not permitted."),
            VaultErrorCode.DocumentNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.DocumentNotFound,
                "Document was not found."),
            VaultErrorCode.PatchTargetNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.PatchTargetNotFound,
                "Patch target was not found."),
            VaultErrorCode.DocumentPathConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DocumentPathConflict,
                "Document path is already in use."),
            VaultErrorCode.BackupInProgress => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.BackupInProgress,
                "A backup operation is already in progress."),
            VaultErrorCode.BackupNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.BackupNotFound,
                "Backup file was not found."),
            VaultErrorCode.RevisionConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RevisionConflict,
                "Document revision changed."),
            VaultErrorCode.PatchTargetAmbiguous => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.PatchTargetAmbiguous,
                "Patch target is ambiguous."),
            VaultErrorCode.VaultAlreadyInitialized => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.VaultAlreadyInitialized,
                "Vault is already initialized."),
            VaultErrorCode.VaultBusy => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.VaultBusy,
                "Vault is transitioning."),
            VaultErrorCode.PurgeNotEligible => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.PurgeNotEligible,
                "Document is not eligible for permanent deletion."),
            VaultErrorCode.DocumentTooLarge => Failure(
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.DocumentTooLarge,
                "Document is too large."),
            VaultErrorCode.BackupTooLarge => Failure(
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.BackupTooLarge,
                "Backup package is too large."),
            VaultErrorCode.VaultLocked => Failure(
                StatusCodes.Status423Locked,
                ApiErrorCodes.VaultLocked,
                "Vault is locked."),
            VaultErrorCode.VaultNotInitialized => Failure(
                StatusCodes.Status423Locked,
                ApiErrorCodes.VaultNotInitialized,
                "Vault is not initialized."),
            VaultErrorCode.VaultLockFailed => Failure(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.VaultLockFailed,
                "Vault lock failed; restart the server."),
            VaultErrorCode.StorageError => Failure(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.StorageError,
                "Vault storage failed."),
            VaultErrorCode.DatabaseError => Failure(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.DatabaseError,
                "Vault database operation failed."),
            _ => Failure(
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "The request failed.")
        };

    private static IReadOnlyDictionary<string, object?> Sanitize(
        IReadOnlyDictionary<string, object?>? details)
    {
        if (details is null || details.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return details
            .Where(pair => SafeDetailKeys.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
    }

    private static ApiFailure Failure(
        int statusCode,
        string code,
        string message) =>
        new(statusCode, code, message);
}
