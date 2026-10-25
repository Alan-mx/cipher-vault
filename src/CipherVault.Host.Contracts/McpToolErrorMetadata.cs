namespace CipherVault.Host.Contracts;

public static class McpToolErrorMetadata
{
    public static bool IsRetryable(string code)
    {
        return code is
            "auth_required" or
            "session_expired" or
            "access_grant_required" or
            "access_grant_invalid" or
            "access_grant_expired" or
            "access_grant_revoked" or
            "access_grant_cooldown" or
            "revision_conflict" or
            "conflict" or
            "maintenance_in_progress" or
            "lock_in_progress" or
            "vault_in_use" or
            "rate_limited" or
            "host_unavailable" or
            "bridge_unavailable" or
            "bridge_connection_reset" or
            "bridge_reauthorization_failed";
    }

    public static string? HintFor(string code)
    {
        return code switch
        {
            "auth_required" => "Unlock the Vault and retry.",
            "session_expired" => "Re-authorize the MCP Bridge and retry.",
            "access_grant_required" or
            "access_grant_invalid" or
            "access_grant_expired" or
            "access_grant_revoked" =>
                "Request a new Access Grant with the required scopes.",
            "access_grant_cooldown" =>
                "Wait until the Grant cooldown expires and request again.",
            "access_denied" =>
                "Request an Access Grant that includes the required scope.",
            "revision_conflict" or "conflict" =>
                "Read the latest revision and retry with expectedRevision.",
            "invalid_cursor" =>
                "Use a cursor returned by the same paginated query.",
            "patch_target_not_found" =>
                "Refresh the document map and verify the heading or frontmatter target.",
            "frontmatter_invalid" =>
                "Correct the YAML or patch value type and retry.",
            "link_index_too_large" =>
                "Reduce the number of links or split the document.",
            "not_found" =>
                "Refresh the catalog and verify the document id.",
            "maintenance_in_progress" =>
                "Wait for the maintenance operation to finish and retry.",
            "lock_in_progress" =>
                "Wait for the Vault lock operation to finish.",
            "vault_in_use" =>
                "Close other Vault processes and retry.",
            "rate_limited" =>
                "Wait for the indicated retry window.",
            "host_unavailable" or
            "bridge_unavailable" or
            "bridge_connection_reset" =>
                "Verify the Host service is running and retry.",
            "bridge_reauthorization_failed" =>
                "Re-authorize the MCP Bridge before retrying.",
            "unsupported_tool" =>
                "Refresh the tool list and use a supported tool.",
            _ => null
        };
    }
}
