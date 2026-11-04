namespace CipherVault.Server.Api;

public static class ApiErrorCodes
{
    public const string InvalidInput = "invalid_input";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidCursor = "invalid_cursor";
    public const string InvalidCredential = "invalid_credential";
    public const string AuthenticationRequired = "authentication_required";
    public const string Forbidden = "forbidden";
    public const string HostInvalid = "host_invalid";
    public const string OriginInvalid = "origin_invalid";
    public const string AntiforgeryInvalid = "antiforgery_invalid";
    public const string DocumentNotFound = "document_not_found";
    public const string PatchTargetNotFound = "patch_target_not_found";
    public const string RouteNotFound = "route_not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string DocumentPathConflict = "document_path_conflict";
    public const string RevisionConflict = "document_revision_conflict";
    public const string DraftRevisionConflict = "draft_revision_conflict";
    public const string DraftNotFound = "draft_not_found";
    public const string DraftNotEditable = "draft_not_editable";
    public const string DraftOperationInvalid = "draft_operation_invalid";
    public const string DraftPayloadTooLarge = "draft_payload_too_large";
    public const string DraftLimitExceeded = "draft_limit_exceeded";
    public const string DraftStorageLimitExceeded = "draft_storage_limit_exceeded";
    public const string WriteModeDraftRequired = "write_mode_draft_required";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string InvalidDocumentId = "invalid_document_id";
    public const string DocumentIdConflict = "document_id_conflict";
    public const string DocumentIdMismatch = "document_id_mismatch";
    public const string DocumentIdProtected = "document_id_protected";
    public const string TrashedDocumentIdConflict = "trashed_document_id_conflict";
    public const string DuplicateDocumentIdInImport =
        "duplicate_document_id_in_import";
    public const string UnsupportedManifestVersion =
        "unsupported_manifest_version";
    public const string InvalidManifest = "invalid_manifest";
    public const string McpWriteModeInvalid = "mcp_write_mode_invalid";
    public const string PatchTargetAmbiguous = "patch_target_ambiguous";
    public const string VaultAlreadyInitialized = "vault_already_initialized";
    public const string VaultBusy = "vault_busy";
    public const string PurgeNotEligible = "purge_not_eligible";
    public const string DocumentTooLarge = "document_too_large";
    public const string RequestTooLarge = "request_too_large";
    public const string VaultLocked = "vault_locked";
    public const string VaultNotInitialized = "vault_not_initialized";
    public const string UnlockRateLimited = "unlock_rate_limited";
    public const string ActivityRateLimited = "activity_rate_limited";
    public const string InvalidToken = "invalid_token";
    public const string McpTokenNotConfigured = "mcp_token_not_configured";
    public const string VaultLockFailed = "vault_lock_failed";
    public const string StorageError = "storage_error";
    public const string DatabaseError = "database_error";
    public const string BackupInProgress = "backup_in_progress";
    public const string BackupTooLarge = "backup_too_large";
    public const string BackupNotFound = "backup_not_found";
    public const string ExportTooLarge = "export_too_large";
    public const string SemanticSearchUnavailable = "semantic_search_unavailable";
    public const string SemanticProfileMismatch = "semantic_profile_mismatch";
    public const string SemanticRemoteNotAllowed = "semantic_remote_not_allowed";
    public const string SemanticRebuildInProgress = "semantic_rebuild_in_progress";
    public const string RelationSuggestionNotFound =
        "relation_suggestion_not_found";
    public const string RelationSuggestionRevisionConflict =
        "relation_suggestion_revision_conflict";
    public const string RelationSuggestionNotEditable =
        "relation_suggestion_not_editable";
    public const string RelationSuggestionExists =
        "relation_suggestion_exists";
    public const string RelationSuggestionLimitExceeded =
        "relation_suggestion_limit_exceeded";
    public const string RelationSuggestionStale =
        "relation_suggestion_stale";
    public const string DocumentRelationNotFound =
        "document_relation_not_found";
    public const string InvalidIfMatch = "invalid_if_match";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string InternalError = "internal_error";
}
