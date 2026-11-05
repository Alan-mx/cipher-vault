using CipherVault.Core.Errors;
using CipherVault.Server.Api.Contracts;
using Microsoft.AspNetCore.Antiforgery;

namespace CipherVault.Server.Api;

public static class ApiErrorMapper
{
    private static readonly HashSet<string> SafeDetailKeys =
    [
        "currentRevision",
        "currentDraftRevision",
        "currentSourceRevision",
        "currentTargetRevision",
        "draftId",
        "sourceId",
        "knowledgeId",
        "relationId",
        "errorCode",
        "limit",
        "maximum",
        "actualBytes",
        "retryAfterSeconds",
        "reasonCode",
        "sessionGeneration",
        "status",
        "actual",
        "expected",
        "fieldPath"
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
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            failure.Code,
            failure.Message,
            Sanitize(details),
            context.GetId());

    public static ApiError CreateVaultError(
        VaultException exception,
        HttpContext context) =>
        CreateError(
            MapVaultException(exception),
            context,
            exception.Details);

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
            VaultErrorCode.InvalidPageSize => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidPageSize,
                "Page size is invalid."),
            VaultErrorCode.KnowledgeModelInvalid => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.KnowledgeModelInvalid,
                "Knowledge Model is invalid."),
            VaultErrorCode.PreconditionFailed => Failure(
                StatusCodes.Status412PreconditionFailed,
                ApiErrorCodes.PreconditionFailed,
                "The resource version changed."),
            VaultErrorCode.PayloadTooLarge => Failure(
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.PayloadTooLarge,
                "Payload is too large."),
            VaultErrorCode.InvalidCredential => Failure(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.InvalidCredential,
                "Vault credential is invalid."),
            VaultErrorCode.Forbidden => Failure(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                "This operation is not permitted."),
            VaultErrorCode.SourceNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.SourceNotFound,
                "Source was not found."),
            VaultErrorCode.SourceRevisionNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.SourceRevisionNotFound,
                "Source revision was not found."),
            VaultErrorCode.SourceLocatorNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.SourceLocatorNotFound,
                "Source locator was not found."),
            VaultErrorCode.KnowledgeNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.KnowledgeNotFound,
                "Knowledge was not found."),
            VaultErrorCode.KnowledgeRevisionNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.KnowledgeRevisionNotFound,
                "Knowledge revision was not found."),
            VaultErrorCode.RelationNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.RelationNotFound,
                "Relation was not found."),
            VaultErrorCode.DraftNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.DraftNotFound,
                "Draft was not found."),
            VaultErrorCode.InvalidSearchMode => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidSearchMode,
                "Search mode is invalid."),
            VaultErrorCode.SearchQueryRequired => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.SearchQueryRequired,
                "A search query or filter is required."),
            VaultErrorCode.SearchQueryTooShort => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.SearchQueryTooShort,
                "Fulltext query is too short."),
            VaultErrorCode.SourceMetadataInvalid => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.SourceMetadataInvalid,
                "Source metadata is invalid."),
            VaultErrorCode.SourceReferenceInvalid => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.SourceReferenceInvalid,
                "Source reference is invalid."),
            VaultErrorCode.RelationAlreadyExists => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RelationAlreadyExists,
                "An active relation already exists."),
            VaultErrorCode.DraftTargetInvalid => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftTargetInvalid,
                "Draft target is invalid."),
            VaultErrorCode.DraftStateConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftStateConflict,
                "Draft state does not allow this operation."),
            VaultErrorCode.DraftBaseRevisionConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftBaseRevisionConflict,
                "Draft base revision changed."),
            VaultErrorCode.DraftRevisionConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftRevisionConflict,
                "Draft revision changed."),
            VaultErrorCode.DraftModelConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftModelConflict,
                "Draft Knowledge Model version changed."),
            VaultErrorCode.FrontMatterModelMismatch => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.FrontMatterModelMismatch,
                "Frontmatter conflicts with structured Knowledge data."),
            VaultErrorCode.ImmutableDefinitionConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.ImmutableDefinitionConflict,
                "A stable definition field cannot be changed."),
            VaultErrorCode.DraftOperationInvalid => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.DraftOperationInvalid,
                "Draft operation is invalid."),
            VaultErrorCode.TagLimitExceeded => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.TagLimitExceeded,
                "Knowledge tags exceed the allowed limit."),
            VaultErrorCode.IdempotencyKeyRequired => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.IdempotencyKeyRequired,
                "An idempotency key is required."),
            VaultErrorCode.IdempotencyKeyReused => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.IdempotencyKeyReused,
                "The idempotency key was used for a different request."),
            VaultErrorCode.BackupInProgress => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.BackupInProgress,
                "A backup operation is already in progress."),
            VaultErrorCode.BackupNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.BackupNotFound,
                "Backup file was not found."),
            VaultErrorCode.BackupTooLarge => Failure(
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.BackupTooLarge,
                "Backup package is too large."),
            VaultErrorCode.VaultAlreadyInitialized => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.VaultAlreadyInitialized,
                "Vault is already initialized."),
            VaultErrorCode.VaultBusy => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.VaultBusy,
                "Vault is transitioning."),
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
        IReadOnlyDictionary<string, object?>? details) =>
        details is null || details.Count == 0
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : details
                .Where(pair => SafeDetailKeys.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);

    private static ApiFailure Failure(
        int statusCode,
        string code,
        string message) =>
        new(statusCode, code, message);
}
