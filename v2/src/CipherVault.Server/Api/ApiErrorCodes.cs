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
    public const string RouteNotFound = "route_not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string DraftRevisionConflict = "draft_revision_conflict";
    public const string DraftNotFound = "draft_not_found";
    public const string DraftOperationInvalid = "draft_operation_invalid";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string TagLimitExceeded = "tag_limit_exceeded";
    public const string VaultAlreadyInitialized = "vault_already_initialized";
    public const string VaultBusy = "vault_busy";
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
    public const string InvalidIfMatch = "invalid_if_match";
    public const string IfMatchRequired = "if_match_required";
    public const string PreconditionFailed = "precondition_failed";
    public const string KnowledgeModelInvalid = "knowledge_model_invalid";
    public const string PayloadTooLarge = "payload_too_large";
    public const string SourceNotFound = "source_not_found";
    public const string SourceRevisionNotFound = "source_revision_not_found";
    public const string SourceLocatorNotFound = "source_locator_not_found";
    public const string KnowledgeNotFound = "knowledge_not_found";
    public const string KnowledgeRevisionNotFound =
        "knowledge_revision_not_found";
    public const string RelationNotFound = "relation_not_found";
    public const string InvalidSearchMode = "invalid_search_mode";
    public const string SearchQueryRequired = "search_query_required";
    public const string SearchQueryTooShort = "search_query_too_short";
    public const string SourceMetadataInvalid = "source_metadata_invalid";
    public const string SourceReferenceInvalid = "source_reference_invalid";
    public const string RelationAlreadyExists = "relation_already_exists";
    public const string DraftTargetInvalid = "draft_target_invalid";
    public const string DraftStateConflict = "draft_state_conflict";
    public const string DraftBaseRevisionConflict =
        "draft_base_revision_conflict";
    public const string DraftModelConflict = "draft_model_conflict";
    public const string FrontMatterModelMismatch =
        "frontmatter_model_mismatch";
    public const string ImmutableDefinitionConflict =
        "immutable_definition_conflict";
    public const string InvalidPageSize = "invalid_page_size";
    public const string InvalidOffset = "invalid_offset";
    public const string InvalidMaxChars = "invalid_max_chars";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string InternalError = "internal_error";
}
