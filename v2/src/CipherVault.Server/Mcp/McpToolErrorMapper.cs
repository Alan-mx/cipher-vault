using CipherVault.Core.Errors;
using CipherVault.Server.Api;
using CipherVault.Server.Mcp.Contracts;

namespace CipherVault.Server.Mcp;

public static class McpToolErrorMapper
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
        "limit",
        "maximum",
        "field",
        "expected",
        "actual",
        "sessionGeneration",
        "status",
        "reasonCode"
    ];

    public static McpToolError Map(
        Exception exception,
        string correlationId) =>
        exception switch
        {
            McpToolRequestException request => FromCode(
                request.Code,
                request.Message,
                request.Details,
                correlationId),
            ApiRequestException request => FromCode(
                request.Code,
                request.Message,
                request.Details,
                correlationId),
            VaultException vault => MapVault(vault, correlationId),
            System.Text.Json.JsonException => FromCode(
                "invalid_input",
                "Tool arguments are invalid.",
                null,
                correlationId),
            _ => FromCode(
                "internal_error",
                "The tool call failed.",
                null,
                correlationId)
        };

    private static McpToolError MapVault(
        VaultException exception,
        string correlationId)
    {
        var code = exception.Code switch
        {
            VaultErrorCode.InvalidInput or VaultErrorCode.InvalidCursor =>
                "invalid_input",
            VaultErrorCode.KnowledgeModelInvalid => "knowledge_model_invalid",
            VaultErrorCode.PreconditionFailed => "precondition_failed",
            VaultErrorCode.PayloadTooLarge => "payload_too_large",
            VaultErrorCode.SourceNotFound => "source_not_found",
            VaultErrorCode.SourceRevisionNotFound =>
                "source_revision_not_found",
            VaultErrorCode.SourceLocatorNotFound => "source_locator_not_found",
            VaultErrorCode.KnowledgeNotFound => "knowledge_not_found",
            VaultErrorCode.KnowledgeRevisionNotFound =>
                "knowledge_revision_not_found",
            VaultErrorCode.RelationNotFound => "relation_not_found",
            VaultErrorCode.InvalidSearchMode => "invalid_search_mode",
            VaultErrorCode.SearchQueryRequired => "search_query_required",
            VaultErrorCode.SearchQueryTooShort => "search_query_too_short",
            VaultErrorCode.SourceMetadataInvalid => "source_metadata_invalid",
            VaultErrorCode.SourceReferenceInvalid => "source_reference_invalid",
            VaultErrorCode.RelationAlreadyExists => "relation_already_exists",
            VaultErrorCode.DraftNotFound => "draft_not_found",
            VaultErrorCode.DraftRevisionConflict => "draft_revision_conflict",
            VaultErrorCode.DraftOperationInvalid => "draft_operation_invalid",
            VaultErrorCode.DraftTargetInvalid => "draft_target_invalid",
            VaultErrorCode.DraftStateConflict => "draft_state_conflict",
            VaultErrorCode.DraftBaseRevisionConflict =>
                "draft_base_revision_conflict",
            VaultErrorCode.DraftModelConflict => "draft_model_conflict",
            VaultErrorCode.FrontMatterModelMismatch =>
                "frontmatter_model_mismatch",
            VaultErrorCode.ImmutableDefinitionConflict =>
                "immutable_definition_conflict",
            VaultErrorCode.TagLimitExceeded => "tag_limit_exceeded",
            VaultErrorCode.IdempotencyKeyRequired =>
                "idempotency_key_required",
            VaultErrorCode.IdempotencyKeyReused => "idempotency_key_reused",
            VaultErrorCode.VaultLocked or VaultErrorCode.VaultNotInitialized =>
                "vault_locked",
            VaultErrorCode.VaultBusy => "vault_busy",
            VaultErrorCode.VaultLockFailed => "vault_lock_failed",
            VaultErrorCode.StorageError or VaultErrorCode.DatabaseError =>
                "database_error",
            _ => "internal_error"
        };
        return FromCode(
            code,
            MessageFor(code),
            exception.Details,
            correlationId);
    }

    private static McpToolError FromCode(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details,
        string correlationId) =>
        new(
            code,
            message,
            Sanitize(details),
            correlationId,
            HintFor(code),
            RetryableFor(code));

    private static IReadOnlyDictionary<string, object?> Sanitize(
        IReadOnlyDictionary<string, object?>? details) =>
        details is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : details
                .Where(pair => SafeDetailKeys.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);

    private static string MessageFor(string code) =>
        code switch
        {
            "invalid_input" => "Tool arguments are invalid.",
            "knowledge_model_invalid" => "Knowledge Model is invalid.",
            "precondition_failed" => "The resource version changed.",
            "payload_too_large" => "Payload is too large.",
            "source_not_found" => "Source was not found.",
            "source_revision_not_found" => "Source revision was not found.",
            "source_locator_not_found" => "Source locator was not found.",
            "knowledge_not_found" => "Knowledge was not found.",
            "knowledge_revision_not_found" =>
                "Knowledge revision was not found.",
            "relation_not_found" => "Relation was not found.",
            "invalid_search_mode" => "Search mode is invalid.",
            "search_query_required" => "A search query or filter is required.",
            "search_query_too_short" =>
                "Fulltext query must contain at least three characters.",
            "source_metadata_invalid" => "Source metadata is invalid.",
            "source_reference_invalid" => "Source reference is invalid.",
            "relation_already_exists" => "An active relation already exists.",
            "draft_not_found" => "Draft was not found.",
            "draft_revision_conflict" => "Draft revision changed.",
            "draft_operation_invalid" => "Draft operation is invalid.",
            "draft_target_invalid" => "Draft target is invalid.",
            "draft_state_conflict" =>
                "Draft state does not allow this operation.",
            "draft_base_revision_conflict" =>
                "Draft base revision changed.",
            "draft_model_conflict" => "Knowledge Model changed.",
            "frontmatter_model_mismatch" =>
                "Frontmatter conflicts with structured Knowledge data.",
            "immutable_definition_conflict" =>
                "A stable definition field cannot be changed.",
            "tag_limit_exceeded" => "Knowledge tags exceed the allowed limit.",
            "idempotency_key_required" => "An idempotency key is required.",
            "idempotency_key_reused" =>
                "The idempotency key was used for a different request.",
            "vault_locked" => "Vault is locked.",
            "vault_busy" => "Vault is transitioning.",
            "vault_lock_failed" => "Vault lock failed; restart the server.",
            "database_error" => "Vault database operation failed.",
            _ => "The tool call failed."
        };

    private static string HintFor(string code) =>
        code switch
        {
            "invalid_input" => "Correct the tool arguments and retry.",
            "knowledge_model_invalid" =>
                "Read the current Knowledge Model and correct the definition.",
            "precondition_failed" => "Read the latest version before retrying.",
            "payload_too_large" => "Reduce the payload size.",
            "source_not_found" => "List sources and use a current sourceId.",
            "source_revision_not_found" =>
                "List source revisions and choose an existing revision.",
            "source_locator_not_found" =>
                "List source locators and use a current locatorId.",
            "knowledge_not_found" =>
                "Search Knowledge and use a current knowledgeId.",
            "knowledge_revision_not_found" =>
                "List Knowledge revisions and choose an existing revision.",
            "relation_not_found" => "Refresh relations and retry.",
            "invalid_search_mode" => "Use keyword or fulltext.",
            "search_query_required" =>
                "Provide a query or at least one type, source, or tag filter.",
            "search_query_too_short" =>
                "Use at least three characters or switch to keyword.",
            "source_metadata_invalid" =>
                "Read the source rule and provide valid metadata.",
            "source_reference_invalid" =>
                "Use a real source revision/locator and an exact excerpt.",
            "relation_already_exists" =>
                "Use the existing active relation or retract it first.",
            "draft_not_found" => "List drafts and use a current draftId.",
            "draft_revision_conflict" =>
                "Read the latest draft revision and retry.",
            "draft_operation_invalid" => "Correct the typed draft payload.",
            "draft_target_invalid" =>
                "Use a current target and its current revision.",
            "draft_state_conflict" =>
                "Read the latest Draft state before retrying.",
            "draft_base_revision_conflict" =>
                "Rebase the Draft against the current target revision.",
            "draft_model_conflict" =>
                "Read the current Knowledge Model and rebase the Draft.",
            "frontmatter_model_mismatch" =>
                "Align Frontmatter with the explicit structured fields.",
            "immutable_definition_conflict" =>
                "Create a new object instead of changing stable identity fields.",
            "tag_limit_exceeded" => "Use fewer or shorter tags.",
            "idempotency_key_required" => "Generate a new UUID idempotencyKey.",
            "idempotency_key_reused" =>
                "Use a new idempotencyKey for a different request.",
            "vault_locked" => "Unlock the Vault in the Web UI before retrying.",
            "vault_busy" => "Wait for the Vault transition to finish.",
            "vault_lock_failed" => "Restart CipherVault.Server.",
            "database_error" => "Retry after checking the Server status.",
            _ => "Check the request and Server logs by correlationId."
        };

    private static bool RetryableFor(string code) =>
        code is "vault_locked" or "vault_busy" or "database_error";
}
