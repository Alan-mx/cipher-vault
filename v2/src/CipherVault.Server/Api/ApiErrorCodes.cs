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
    public const string RevisionConflict = "revision_conflict";
    public const string PatchTargetAmbiguous = "patch_target_ambiguous";
    public const string VaultAlreadyInitialized = "vault_already_initialized";
    public const string VaultBusy = "vault_busy";
    public const string PurgeNotEligible = "purge_not_eligible";
    public const string DocumentTooLarge = "document_too_large";
    public const string RequestTooLarge = "request_too_large";
    public const string VaultLocked = "vault_locked";
    public const string VaultNotInitialized = "vault_not_initialized";
    public const string UnlockRateLimited = "unlock_rate_limited";
    public const string VaultLockFailed = "vault_lock_failed";
    public const string StorageError = "storage_error";
    public const string DatabaseError = "database_error";
    public const string InvalidIfMatch = "invalid_if_match";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string InternalError = "internal_error";
}
