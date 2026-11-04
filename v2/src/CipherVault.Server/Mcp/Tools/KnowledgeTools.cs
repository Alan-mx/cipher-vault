using System.ComponentModel;
using System.Text.Json;
using CipherVault.Application.Documents;
using CipherVault.Application.Graph;
using CipherVault.Application.Search;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeGraph;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class KnowledgeTools
{
    private readonly DocumentService documents;
    private readonly SearchService search;
    private readonly GraphService graph;
    private readonly McpToolInvoker invoker;

    public KnowledgeTools(
        DocumentService documents,
        SearchService search,
        GraphService graph,
        McpToolInvoker invoker)
    {
        this.documents = documents;
        this.search = search;
        this.graph = graph;
        this.invoker = invoker;
    }

    [McpServerTool(Name = McpToolNames.SearchNotes)]
    [Description("Search active documents by optional keyword and/or structured filters. Empty keywords are allowed when at least one filter is active. Short keyword queries scan at most 10000 recent documents; pure filter queries use direct database filtering.")]
    public Task<CallToolResult> SearchNotes(
        string? query = null,
        string[]? tags = null,
        string? format = null,
        DateTimeOffset? updatedAfter = null,
        DateTimeOffset? updatedBefore = null,
        bool? hasBacklinks = null,
        bool orphanOnly = false,
        string? searchMode = null,
        JsonElement[]? propertyFilters = null,
        string propertyMatch = "all",
        string? cursor = null,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.SearchNotes,
            async (_, token) =>
            {
                return await search.SearchAsync(
                    new SearchQuery(
                        query ?? string.Empty,
                        ParseSearchMode(searchMode),
                        tags,
                        DocumentReadTools.ParseFormat(format),
                        updatedAfter,
                        updatedBefore,
                        hasBacklinks,
                        orphanOnly,
                        new CursorPageRequest(pageSize, cursor),
                        propertyFilters?
                            .Select(ParsePropertyFilter)
                            .ToArray(),
                        ParsePropertyMatch(propertyMatch)),
                    token);
            },
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ListTags)]
    [Description("List active document tags and usage counts with cursor pagination.")]
    public Task<CallToolResult> ListTags(
        string? cursor = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListTags,
            async (_, token) => await documents.ListTagsAsync(
                new CursorPageRequest(pageSize, cursor),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.GetDocumentMap)]
    [Description("Return bounded document headings, Frontmatter fields, outgoing links and backlink count without content.")]
    public Task<CallToolResult> GetDocumentMap(
        string documentId,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.GetDocumentMap,
            async (_, token) =>
            {
                var map = await documents.GetMapAsync(
                    DocumentReadTools.ParseDocumentId(documentId),
                    token);
                return McpDocumentMapProjector.Project(map);
            },
            cancellationToken);

    [McpServerTool(Name = McpToolNames.GetBacklinks)]
    [Description("Return active documents that link to the requested document with cursor pagination.")]
    public Task<CallToolResult> GetBacklinks(
        string documentId,
        string? cursor = null,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.GetBacklinks,
            async (_, token) => await documents.ListBacklinksAsync(
                DocumentReadTools.ParseDocumentId(documentId),
                new CursorPageRequest(pageSize, cursor),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.FindUnlinkedMentions)]
    [Description("Find plain-text body mentions of a document title that are not WikiLinks.")]
    public Task<CallToolResult> FindUnlinkedMentions(
        string documentId,
        string? cursor = null,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.FindUnlinkedMentions,
            async (_, token) => await documents.ListUnlinkedMentionsAsync(
                DocumentReadTools.ParseDocumentId(documentId),
                new CursorPageRequest(pageSize, cursor),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.GetLocalGraph)]
    [Description("Return a bounded local directed graph around one active document. Tags use AND semantics.")]
    public Task<CallToolResult> GetLocalGraph(
        string documentId,
        int depth = 2,
        string[]? tags = null,
        string? folder = null,
        int maxNodes = 200,
        int maxEdges = 400,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.GetLocalGraph,
            async (_, token) => await graph.GetLocalGraphAsync(
                new LocalGraphQuery(
                    DocumentReadTools.ParseDocumentId(documentId),
                    depth,
                    tags,
                    folder,
                    maxNodes,
                    maxEdges),
                token),
            cancellationToken);

    private static McpToolRequestException InvalidInput(string message) =>
        new("invalid_input", message);

    private static SearchPropertyFilter ParsePropertyFilter(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("field", out var fieldElement)
            || fieldElement.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("operator", out var operatorElement)
            || operatorElement.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("value", out var valueElement))
        {
            throw InvalidInput(
                "Each property filter requires field, operator and value.");
        }

        var operation = operatorElement.GetString() switch
        {
            "exists" => SearchPropertyOperator.Exists,
            "eq" => SearchPropertyOperator.Eq,
            "contains" => SearchPropertyOperator.Contains,
            "in" => SearchPropertyOperator.In,
            "gte" => SearchPropertyOperator.Gte,
            "lte" => SearchPropertyOperator.Lte,
            _ => throw InvalidInput("Property operator is invalid.")
        };
        return new SearchPropertyFilter(
            fieldElement.GetString() ?? string.Empty,
            operation,
            valueElement.Clone());
    }

    private static SearchPropertyMatch ParsePropertyMatch(string value) =>
        value switch
        {
            "all" => SearchPropertyMatch.All,
            "any" => SearchPropertyMatch.Any,
            _ => throw InvalidInput("propertyMatch must be all or any.")
        };

    private static SearchMode ParseSearchMode(string? value) =>
        value switch
        {
            null or "keyword" => SearchMode.Keyword,
            "semantic" => SearchMode.Semantic,
            "hybrid" => SearchMode.Hybrid,
            _ => throw InvalidInput("searchMode is invalid.")
        };
}
