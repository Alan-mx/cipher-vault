using System.Text.Json;
using System.Text.Json.Nodes;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Mcp;

public sealed record BridgePathAccess(
    AccessGrantScope Scopes,
    AccessGrantPathPolicy Paths)
{
    public bool Restricted => Paths.HasScopedPaths;
    public bool ReadRestricted => !Paths.GlobalRead;

    public bool AllowsRead(string virtualPath)
    {
        return (Scopes & AccessGrantScope.Read) != 0 &&
            Paths.AllowsRead(virtualPath);
    }

    public bool AllowsWrite(string virtualPath)
    {
        return (Scopes & AccessGrantScope.Write) != 0 &&
            Paths.AllowsWrite(virtualPath);
    }

    public bool AllowsDelete(string virtualPath)
    {
        return (Scopes & AccessGrantScope.Delete) != 0 &&
            Paths.AllowsDelete(virtualPath);
    }

    public static BridgePathAccess From(AccessGrantContext grant)
    {
        return new BridgePathAccess(
            grant.Scopes,
            grant.PathPolicy ?? AccessGrantPathPolicy.Unrestricted);
    }
}

public static class BridgePathAccessGuard
{
    public static bool RequiresDocumentLookup(string toolName)
    {
        return toolName is
            "read_note" or
            "get_document_map" or
            "get_backlinks" or
            "find_unlinked_mentions" or
            "get_local_graph" or
            "append_note" or
            "update_note" or
            "patch_note" or
            "delete_note";
    }

    public static bool IsWriteTool(string toolName)
    {
        return toolName is
            "append_note" or
            "update_note" or
            "patch_note";
    }

    public static JsonElement FilterResult(
        string toolName,
        JsonElement result,
        BridgePathAccess access)
    {
        if (!access.ReadRestricted)
        {
            return result;
        }
        if (toolName == "tag_list")
        {
            throw new BridgeToolException(
                "access_denied",
                "tag_list is unavailable while using folder-scoped access.");
        }
        var root = JsonNode.Parse(result.GetRawText()) as JsonObject;
        if (root is null)
        {
            return result;
        }
        switch (toolName)
        {
            case "list_catalog":
            case "scan_notes":
                FilterArray(root, "items", "virtualPath", access);
                break;
            case "search_notes":
                FilterSearch(root, access);
                break;
            case "get_backlinks":
            case "find_unlinked_mentions":
                FilterArray(root, "items", "sourceVirtualPath", access);
                break;
            case "get_local_graph":
                FilterGraph(root, access);
                break;
        }
        return JsonSerializer.SerializeToElement(root);
    }

    private static void FilterSearch(JsonObject root, BridgePathAccess access)
    {
        var returned = FilterArray(root, "hits", "virtualPath", access);
        if (root["coverage"] is not JsonObject coverage)
        {
            return;
        }
        var original = coverage["returnedDocuments"]?.GetValue<int>() ?? returned;
        coverage["returnedDocuments"] = returned;
        coverage["matchedDocuments"] = returned;
        coverage["isTruncated"] =
            coverage["isTruncated"]?.GetValue<bool>() == true ||
            original != returned;
    }

    private static int FilterArray(
        JsonObject root,
        string property,
        string pathProperty,
        BridgePathAccess access)
    {
        if (root[property] is not JsonArray values)
        {
            return 0;
        }
        var filtered = new JsonArray();
        foreach (var value in values)
        {
            if (value is JsonObject item &&
                item[pathProperty]?.GetValue<string>() is { } path &&
                access.AllowsRead(path))
            {
                filtered.Add(item.DeepClone());
            }
        }
        root[property] = filtered;
        return filtered.Count;
    }

    private static void FilterGraph(
        JsonObject root,
        BridgePathAccess access)
    {
        if (root["nodes"] is not JsonArray nodes)
        {
            return;
        }
        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        var filteredNodes = new JsonArray();
        foreach (var node in nodes)
        {
            if (node is not JsonObject item ||
                item["virtualPath"]?.GetValue<string>() is not { } path ||
                !access.AllowsRead(path))
            {
                continue;
            }
            if (item["documentId"]?.GetValue<string>() is { } id)
            {
                visibleIds.Add(id);
            }
            filteredNodes.Add(item.DeepClone());
        }
        root["nodes"] = filteredNodes;

        if (root["edges"] is not JsonArray edges)
        {
            return;
        }
        var filteredEdges = new JsonArray();
        foreach (var edge in edges)
        {
            if (edge is JsonObject item &&
                item["sourceDocumentId"]?.GetValue<string>() is { } source &&
                item["targetDocumentId"]?.GetValue<string>() is { } target &&
                visibleIds.Contains(source) &&
                visibleIds.Contains(target))
            {
                filteredEdges.Add(item.DeepClone());
            }
        }
        root["edges"] = filteredEdges;
        if (nodes.Count != filteredNodes.Count || edges.Count != filteredEdges.Count)
        {
            root["truncated"] = true;
        }
    }
}
