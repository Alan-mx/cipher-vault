using System.Text.Json;
using CipherVault.Host.Mcp;
using CipherVault.Host.Security;

namespace CipherVault.Mcp.Tests;

public sealed class BridgePathAccessGuardTests
{
    [Fact]
    public void CatalogAndSearchResultsAreFilteredByReadPaths()
    {
        Assert.True(AccessGrantPathPolicy.TryParse(
            ["read:Projects"],
            out var scopes,
            out var paths));
        var access = new BridgePathAccess(scopes, paths);

        var catalog = BridgePathAccessGuard.FilterResult(
            "list_catalog",
            JsonSerializer.SerializeToElement(new
            {
                items = new[]
                {
                    new { title = "visible", virtualPath = "Projects/a.md" },
                    new { title = "hidden", virtualPath = "Other/b.md" }
                },
                nextCursor = (string?)null
            }),
            access);
        Assert.Equal(
            "visible",
            Assert.Single(catalog.GetProperty("items").EnumerateArray())
                .GetProperty("title")
                .GetString());

        var search = BridgePathAccessGuard.FilterResult(
            "search_notes",
            JsonSerializer.SerializeToElement(new
            {
                hits = new[]
                {
                    new { title = "visible", virtualPath = "Projects/a.md" },
                    new { title = "hidden", virtualPath = "Other/b.md" }
                },
                coverage = new
                {
                    returnedDocuments = 2,
                    matchedDocuments = 2,
                    isTruncated = false
                }
            }),
            access);
        Assert.Single(search.GetProperty("hits").EnumerateArray());
        Assert.True(
            search.GetProperty("coverage")
                .GetProperty("isTruncated")
                .GetBoolean());
    }

    [Fact]
    public void TagListIsDeniedWhenPathScoped()
    {
        Assert.True(AccessGrantPathPolicy.TryParse(
            ["read:Projects"],
            out var scopes,
            out var paths));
        var access = new BridgePathAccess(scopes, paths);

        var exception = Assert.Throws<BridgeToolException>(() =>
            BridgePathAccessGuard.FilterResult(
                "tag_list",
                JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() }),
                access));

        Assert.Equal("access_denied", exception.Code);
    }

    [Fact]
    public void GlobalReadAllowsTagListWhenOnlyWriteIsScoped()
    {
        Assert.True(AccessGrantPathPolicy.TryParse(
            ["read", "write:Inbox"],
            out var scopes,
            out var paths));
        var access = new BridgePathAccess(scopes, paths);
        var original = JsonSerializer.SerializeToElement(new
        {
            items = new[] { new { name = "all-tags", documentCount = 3 } }
        });

        var result = BridgePathAccessGuard.FilterResult(
            "tag_list",
            original,
            access);

        Assert.Equal(original.GetRawText(), result.GetRawText());
        Assert.False(access.ReadRestricted);
    }

    [Fact]
    public void GraphEdgesAreRemovedWithHiddenNodes()
    {
        Assert.True(AccessGrantPathPolicy.TryParse(
            ["read:Projects"],
            out var scopes,
            out var paths));
        var access = new BridgePathAccess(scopes, paths);

        var graph = BridgePathAccessGuard.FilterResult(
            "get_local_graph",
            JsonSerializer.SerializeToElement(new
            {
                nodes = new[]
                {
                    new { documentId = "a", virtualPath = "Projects/a.md" },
                    new { documentId = "b", virtualPath = "Other/b.md" }
                },
                edges = new[]
                {
                    new { sourceDocumentId = "a", targetDocumentId = "b" }
                },
                truncated = false
            }),
            access);

        Assert.Single(graph.GetProperty("nodes").EnumerateArray());
        Assert.Empty(graph.GetProperty("edges").EnumerateArray());
        Assert.True(graph.GetProperty("truncated").GetBoolean());
    }
}
