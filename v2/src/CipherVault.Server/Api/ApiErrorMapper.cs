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
        "currentSuggestionRevision",
        "currentSourceRevision",
        "currentTargetRevision",
        "candidateHeadingPaths",
        "documentId",
        "draftId",
        "suggestionId",
        "relationId",
        "revisionId",
        "errorCode",
        "importItemIndex",
        "limit",
        "maximum",
        "actualBytes",
        "purgeEligibleAt",
        "retryAfterSeconds",
        "reasonCode",
        "mode",
        "sessionGeneration",
        "status",
        "actual",
        "expected",
        "identitySource",
        "format"
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
            VaultErrorCode.SemanticSearchUnavailable =>
                SemanticUnavailableFailure(exception),
            VaultErrorCode.SemanticProfileMismatch => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.SemanticProfileMismatch,
                "Semantic profile changed."),
            VaultErrorCode.SemanticRemoteNotAllowed => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.SemanticRemoteNotAllowed,
                "Remote semantic embedding is not allowed."),
            VaultErrorCode.SemanticRebuildInProgress => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.SemanticRebuildInProgress,
                "Semantic index rebuild is already in progress."),
            VaultErrorCode.RelationSuggestionNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.RelationSuggestionNotFound,
                "Relation suggestion was not found."),
            VaultErrorCode.RelationSuggestionRevisionConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RelationSuggestionRevisionConflict,
                "Relation suggestion revision changed."),
            VaultErrorCode.RelationSuggestionNotEditable => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RelationSuggestionNotEditable,
                "Relation suggestion status does not allow this operation."),
            VaultErrorCode.RelationSuggestionExists => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RelationSuggestionExists,
                "A pending relation suggestion already exists."),
            VaultErrorCode.RelationSuggestionLimitExceeded => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RelationSuggestionLimitExceeded,
                "The active relation suggestion limit was reached."),
            VaultErrorCode.RelationSuggestionStale => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.RelationSuggestionStale,
                "Relation suggestion source or target changed."),
            VaultErrorCode.DocumentRelationNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.DocumentRelationNotFound,
                "Active document relation was not found."),
            VaultErrorCode.DraftRevisionConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftRevisionConflict,
                "Document draft revision changed."),
            VaultErrorCode.DraftNotFound => Failure(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.DraftNotFound,
                "Document draft was not found."),
            VaultErrorCode.DraftNotEditable => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftNotEditable,
                "Document draft state does not allow this operation."),
            VaultErrorCode.DraftOperationInvalid => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.DraftOperationInvalid,
                "Document draft operation is invalid."),
            VaultErrorCode.DraftPayloadTooLarge => Failure(
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.DraftPayloadTooLarge,
                "Document draft payload is too large."),
            VaultErrorCode.DraftLimitExceeded => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftLimitExceeded,
                "The active document draft limit was reached."),
            VaultErrorCode.DraftStorageLimitExceeded => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DraftStorageLimitExceeded,
                "The active document draft storage limit was reached."),
            VaultErrorCode.WriteModeDraftRequired => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.WriteModeDraftRequired,
                "MCP document writes require Direct Write Mode."),
            VaultErrorCode.IdempotencyKeyRequired => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.IdempotencyKeyRequired,
                "An idempotency key is required."),
            VaultErrorCode.IdempotencyKeyReused => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.IdempotencyKeyReused,
                "The idempotency key was used for a different request."),
            VaultErrorCode.InvalidDocumentId => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidDocumentId,
                "Document ID is invalid."),
            VaultErrorCode.DocumentIdConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DocumentIdConflict,
                "Document ID is already in use."),
            VaultErrorCode.DocumentIdMismatch => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DocumentIdMismatch,
                "Document ID does not match document content."),
            VaultErrorCode.DocumentIdProtected => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.DocumentIdProtected,
                "Document ID is protected."),
            VaultErrorCode.TrashedDocumentIdConflict => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.TrashedDocumentIdConflict,
                "Document ID belongs to a trashed document."),
            VaultErrorCode.DuplicateDocumentIdInImport => Failure(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.DuplicateDocumentIdInImport,
                "The import batch contains duplicate document IDs."),
            VaultErrorCode.UnsupportedManifestVersion => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.UnsupportedManifestVersion,
                "Import Manifest version is not supported."),
            VaultErrorCode.InvalidManifest => Failure(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidManifest,
                "Import Manifest is invalid."),
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
            VaultErrorCode.ExportTooLarge => Failure(
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.ExportTooLarge,
                "Plaintext export is too large."),
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

    private static ApiFailure SemanticUnavailableFailure(
        VaultException exception)
    {
        var statusCode = exception.Details.TryGetValue(
                "reasonCode",
                out var reason)
            && reason is string reasonCode
            && reasonCode is
                "provider_unavailable"
                or "provider_timeout"
                or "provider_rate_limited"
                or "provider_invalid_response"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status409Conflict;
        return Failure(
            statusCode,
            ApiErrorCodes.SemanticSearchUnavailable,
            "Semantic search is unavailable.");
    }
}
