using CipherVault.Core.Errors;
using CipherVault.Server.Api;
using CipherVault.Server.Mcp.Contracts;

namespace CipherVault.Server.Mcp;

public static class McpToolErrorMapper
{
    private static readonly HashSet<string> SafeDetailKeys =
    [
        "currentRevision",
        "documentId",
        "revisionId",
        "candidateHeadingPaths",
        "limit",
        "maximum",
        "field",
        "expected",
        "actual"
    ];

    public static McpToolError Map(
        Exception exception,
        string correlationId) =>
        exception switch
        {
            McpToolRequestException request => new(
                request.Code,
                request.Message,
                Sanitize(request.Details),
                correlationId,
                HintFor(request.Code),
                RetryableFor(request.Code)),
            VaultException vault => MapVault(vault, correlationId),
            System.Text.Json.JsonException => new(
                "invalid_input",
                "Tool arguments are invalid.",
                new Dictionary<string, object?>(StringComparer.Ordinal),
                correlationId,
                HintFor("invalid_input"),
                false),
            _ => new(
                "internal_error",
                "The tool call failed.",
                new Dictionary<string, object?>(StringComparer.Ordinal),
                correlationId,
                HintFor("internal_error"),
                false)
        };

    private static McpToolError MapVault(
        VaultException exception,
        string correlationId)
    {
        var code = exception.Code switch
        {
            VaultErrorCode.DocumentNotFound => "not_found",
            VaultErrorCode.RevisionConflict => "revision_conflict",
            VaultErrorCode.InvalidInput => "invalid_input",
            VaultErrorCode.InvalidCursor => "invalid_input",
            VaultErrorCode.DocumentPathConflict => "path_conflict",
            VaultErrorCode.PatchTargetNotFound => "patch_target_not_found",
            VaultErrorCode.PatchTargetAmbiguous => "patch_target_ambiguous",
            VaultErrorCode.DocumentTooLarge => "document_too_large",
            VaultErrorCode.TagLimitExceeded => "tag_limit_exceeded",
            VaultErrorCode.FrontMatterInvalid => "frontmatter_invalid",
            VaultErrorCode.VaultLocked => "vault_locked",
            VaultErrorCode.VaultNotInitialized => "vault_locked",
            VaultErrorCode.VaultBusy => "vault_busy",
            VaultErrorCode.VaultLockFailed => "vault_lock_failed",
            VaultErrorCode.StorageError => "database_error",
            VaultErrorCode.DatabaseError => "database_error",
            _ => "internal_error"
        };
        return new McpToolError(
            code,
            MessageFor(code),
            Sanitize(exception.Details),
            correlationId,
            HintFor(code),
            RetryableFor(code));
    }

    private static IReadOnlyDictionary<string, object?> Sanitize(
        IReadOnlyDictionary<string, object?> details) =>
        details
            .Where(pair => SafeDetailKeys.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);

    private static string MessageFor(string code) =>
        code switch
        {
            "not_found" => "Document was not found.",
            "revision_conflict" => "Document revision changed.",
            "invalid_input" => "Tool arguments are invalid.",
            "invalid_offset" => "Document content offset is invalid.",
            "path_conflict" => "Document path is already in use.",
            "patch_target_not_found" => "Patch target was not found.",
            "patch_target_ambiguous" => "Patch target is ambiguous.",
            "document_too_large" => "Document is too large.",
            "tag_limit_exceeded" => "Document tags exceed the allowed limit.",
            "frontmatter_invalid" => "Markdown Frontmatter is invalid.",
            "vault_locked" => "Vault is locked.",
            "vault_busy" => "Vault is transitioning.",
            "vault_lock_failed" => "Vault lock failed; restart the server.",
            "database_error" => "Vault database operation failed.",
            _ => "The tool call failed."
        };

    private static string HintFor(string code) =>
        code switch
        {
            "not_found" => "List or search active documents to find a current documentId.",
            "revision_conflict" => "Read the latest revision and retry with currentRevision.",
            "invalid_input" => "Correct the tool arguments and retry.",
            "invalid_offset" => "Use an offset within the document and outside a surrogate pair.",
            "path_conflict" => "Choose a different virtual path or update the existing document.",
            "patch_target_not_found" => "Read the document map and choose an existing target.",
            "patch_target_ambiguous" => "Use a more specific heading path.",
            "document_too_large" => "Reduce the content size.",
            "tag_limit_exceeded" => "Use fewer or shorter tags.",
            "frontmatter_invalid" => "Correct the Markdown Frontmatter.",
            "vault_locked" => "Unlock the Vault in the Web UI before retrying.",
            "vault_busy" => "Wait for the Vault transition to finish.",
            "vault_lock_failed" => "Restart CipherVault.Server.",
            "database_error" => "Retry after checking the Server status.",
            _ => "Check the request and Server logs by correlationId."
        };

    private static bool RetryableFor(string code) =>
        code is "vault_locked" or "vault_busy" or "database_error";
}
