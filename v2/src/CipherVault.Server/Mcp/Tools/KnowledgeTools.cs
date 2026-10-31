using System.ComponentModel;
using CipherVault.Application.Documents;
using CipherVault.Application.Graph;
using CipherVault.Application.Search;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeGraph;
using CipherVault.Core.Search;
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
    [Description("Search active Markdown/TXT documents with optional tag, format, time, backlink and orphan filters. Short queries scan at most the 10000 most recently updated documents.")]
    public Task<CallToolResult> SearchNotes(
        string query,
        string[]? tags = null,
        string? format = null,
        DateTimeOffset? updatedAfter = null,
        DateTimeOffset? updatedBefore = null,
        bool? hasBacklinks = null,
        bool orphanOnly = false,
        string? cursor = null,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.SearchNotes,
            async (_, token) =>
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    throw InvalidInput("query is required.");
                }

                return await search.SearchAsync(
                    new SearchQuery(
                        query,
                        tags,
                        DocumentReadTools.ParseFormat(format),
                        updatedAfter,
                        updatedBefore,
                        hasBacklinks,
                        orphanOnly,
                        new CursorPageRequest(pageSize, cursor)),
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
}
