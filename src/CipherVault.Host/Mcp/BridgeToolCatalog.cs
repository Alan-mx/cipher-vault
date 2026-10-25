using CipherVault.Host.Security;

namespace CipherVault.Host.Mcp;

public enum BridgeToolResultKind
{
    VaultStatus,
    Page,
    Document,
    Structured
}

public sealed record BridgeToolDefinition(
    string ToolName,
    AccessGrantScope RequiredScope,
    BridgeToolResultKind ResultKind,
    string? AdminMethod);

public static class BridgeToolCatalog
{
    private static readonly Dictionary<string, BridgeToolDefinition> Definitions =
        new(StringComparer.Ordinal)
        {
            ["vault_status"] = new BridgeToolDefinition(
                "vault_status",
                AccessGrantScope.Read,
                BridgeToolResultKind.VaultStatus,
                AdminMethod: null),
            ["list_catalog"] = new BridgeToolDefinition(
                "list_catalog",
                AccessGrantScope.Read,
                BridgeToolResultKind.Page,
                "admin.catalog.list"),
            ["search_notes"] = new BridgeToolDefinition(
                "search_notes",
                AccessGrantScope.Read,
                BridgeToolResultKind.Page,
                "admin.search.notes"),
            ["read_note"] = new BridgeToolDefinition(
                "read_note",
                AccessGrantScope.Read,
                BridgeToolResultKind.Document,
                "admin.notes.read"),
            ["scan_notes"] = new BridgeToolDefinition(
                "scan_notes",
                AccessGrantScope.Read,
                BridgeToolResultKind.Page,
                "admin.notes.scan"),
            ["get_document_map"] = new BridgeToolDefinition(
                "get_document_map",
                AccessGrantScope.Read,
                BridgeToolResultKind.Structured,
                "admin.notes.map"),
            ["get_backlinks"] = new BridgeToolDefinition(
                "get_backlinks",
                AccessGrantScope.Read,
                BridgeToolResultKind.Page,
                "admin.notes.backlinks"),
            ["find_unlinked_mentions"] = new BridgeToolDefinition(
                "find_unlinked_mentions",
                AccessGrantScope.Read,
                BridgeToolResultKind.Page,
                "admin.notes.unlinked-mentions"),
            ["tag_list"] = new BridgeToolDefinition(
                "tag_list",
                AccessGrantScope.Read,
                BridgeToolResultKind.Page,
                "admin.tags.list"),
            ["get_local_graph"] = new BridgeToolDefinition(
                "get_local_graph",
                AccessGrantScope.Read,
                BridgeToolResultKind.Structured,
                "admin.graph.local"),
            ["create_note"] = new BridgeToolDefinition(
                "create_note",
                AccessGrantScope.Write,
                BridgeToolResultKind.Document,
                "admin.notes.create"),
            ["append_note"] = new BridgeToolDefinition(
                "append_note",
                AccessGrantScope.Write,
                BridgeToolResultKind.Document,
                "admin.notes.append"),
            ["update_note"] = new BridgeToolDefinition(
                "update_note",
                AccessGrantScope.Write,
                BridgeToolResultKind.Document,
                "admin.notes.update"),
            ["patch_note"] = new BridgeToolDefinition(
                "patch_note",
                AccessGrantScope.Write,
                BridgeToolResultKind.Structured,
                "admin.notes.patch"),
            ["delete_note"] = new BridgeToolDefinition(
                "delete_note",
                AccessGrantScope.Delete,
                BridgeToolResultKind.Document,
                "admin.notes.delete")
        };

    public static IReadOnlyList<BridgeToolDefinition> All { get; } =
        Definitions.Values.ToList();

    public static bool TryGet(
        string toolName,
        out BridgeToolDefinition definition)
    {
        return Definitions.TryGetValue(toolName, out definition!);
    }
}
