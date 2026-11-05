using System.ComponentModel;
using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.KnowledgeBase;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class KnowledgeBaseReadTools(
    KnowledgeModelService models,
    SourceService sources,
    KnowledgeService knowledge,
    UnifiedDraftService drafts,
    McpToolInvoker invoker)
{
    [McpServerTool(Name = McpToolNames.GetKnowledgeModel)]
    [Description("Return the current configurable CipherVault knowledge model.")]
    public Task<CallToolResult> GetKnowledgeModel(
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.GetKnowledgeModel,
            async (_, token) => await models.GetAsync(token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ListSources)]
    [Description("List sources with bounded metadata and cursor pagination.")]
    public Task<CallToolResult> ListSources(
        string? status = null,
        string? kind = null,
        string? query = null,
        int pageSize = 20,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListSources,
            async (_, token) => await sources.ListAsync(
                new SourceQuery(
                    ParseSourceStatus(status),
                    ParseSourceKind(kind),
                    query,
                    pageSize,
                    cursor),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ReadSource)]
    [Description("Read source metadata, selected revision metadata, and a bounded content chunk.")]
    public Task<CallToolResult> ReadSource(
        string sourceId,
        int? revision = null,
        int offset = 0,
        int maxChars = 32768,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ReadSource,
            async (_, token) =>
            {
                var id = ParseGuid(sourceId, "sourceId");
                var source = await sources.GetAsync(id, token);
                var selectedRevision = revision ?? source.CurrentRevision;
                var revisionInfo = await sources.GetRevisionAsync(
                    id,
                    selectedRevision,
                    token);
                var content = await sources.ReadContentAsync(
                    id,
                    selectedRevision,
                    offset,
                    maxChars,
                    token);
                return new SourceReadResult(source, revisionInfo, content);
            },
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ListSourceRevisions)]
    [Description("List immutable source revision metadata.")]
    public Task<CallToolResult> ListSourceRevisions(
        string sourceId,
        int pageSize = 20,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListSourceRevisions,
            async (_, token) => await sources.ListRevisionsAsync(
                ParseGuid(sourceId, "sourceId"),
                pageSize,
                cursor,
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ListSourceLocators)]
    [Description("List section or paragraph locators for a source revision.")]
    public Task<CallToolResult> ListSourceLocators(
        string sourceId,
        int revision,
        string? locatorType = null,
        int pageSize = 20,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListSourceLocators,
            async (_, token) => await sources.ListLocatorsAsync(
                ParseGuid(sourceId, "sourceId"),
                revision,
                ParseLocatorType(locatorType),
                pageSize,
                cursor,
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ReadSourceLocator)]
    [Description("Read a bounded text range relative to a source locator.")]
    public Task<CallToolResult> ReadSourceLocator(
        string sourceId,
        int revision,
        string locatorId,
        int offset = 0,
        int maxChars = 32768,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ReadSourceLocator,
            async (_, token) => await sources.ReadLocatorAsync(
                ParseGuid(sourceId, "sourceId"),
                revision,
                ParseGuid(locatorId, "locatorId"),
                offset,
                maxChars,
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.SearchKnowledge)]
    [Description("Search Knowledge using keyword or fulltext mode.")]
    public Task<CallToolResult> SearchKnowledge(
        string? query = null,
        string mode = "keyword",
        string[]? typeIds = null,
        string[]? sourceIds = null,
        string[]? tags = null,
        DateTimeOffset? updatedAfter = null,
        DateTimeOffset? updatedBefore = null,
        int pageSize = 20,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.SearchKnowledge,
            async (_, token) => await knowledge.SearchAsync(
                new KnowledgeSearchQuery(
                    query ?? string.Empty,
                    ParseSearchMode(mode),
                    typeIds?.Select(item => ParseGuid(item, "typeIds")).ToArray(),
                    sourceIds?
                        .Select(item => ParseGuid(item, "sourceIds"))
                        .ToArray(),
                    tags,
                    updatedAfter,
                    updatedBefore,
                    pageSize,
                    cursor),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ReadKnowledge)]
    [Description("Read Knowledge metadata, source references, and a bounded Markdown chunk.")]
    public Task<CallToolResult> ReadKnowledge(
        string knowledgeId,
        int? revision = null,
        int offset = 0,
        int maxChars = 32768,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ReadKnowledge,
            async (_, token) =>
            {
                var id = ParseGuid(knowledgeId, "knowledgeId");
                var selectedRevision = revision
                    ?? (await knowledge.GetAsync(id, token)).CurrentRevision;
                var revisionDetail =
                    await knowledge.GetRevisionAsync(
                        id,
                        selectedRevision,
                        token);
                var content = await knowledge.ReadContentAsync(
                    id,
                    selectedRevision,
                    offset,
                    maxChars,
                    token);
                return new KnowledgeReadResult(revisionDetail, content);
            },
            cancellationToken);

    [McpServerTool(Name = McpToolNames.GetLocalGraph)]
    [Description("Return a bounded local graph built from reviewed relations.")]
    public Task<CallToolResult> GetLocalGraph(
        string centerKnowledgeId,
        int depth = 2,
        int maxNodes = 200,
        int maxEdges = 400,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.GetLocalGraph,
            async (_, token) => await knowledge.GetLocalGraphAsync(
                ParseGuid(centerKnowledgeId, "centerKnowledgeId"),
                depth,
                maxNodes,
                maxEdges,
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ListDrafts)]
    [Description("List unified Knowledge, Source, and Relation drafts.")]
    public Task<CallToolResult> ListDrafts(
        string? status = null,
        string? targetObjectType = null,
        string? operationType = null,
        string? targetId = null,
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListDrafts,
            async (_, token) => await drafts.ListAsync(
                new DraftQuery(
                    ParseDraftStatus(status),
                    ParseTargetType(targetObjectType),
                    ParseOperationType(operationType),
                    null,
                    targetId is null ? null : ParseGuid(targetId, "targetId"),
                    null,
                    null,
                    pageSize,
                    cursor),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ReadDraft)]
    [Description("Read a unified Draft and its bounded preview.")]
    public Task<CallToolResult> ReadDraft(
        string draftId,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ReadDraft,
            async (_, token) => await drafts.GetAsync(
                ParseGuid(draftId, "draftId"),
                token),
            cancellationToken);

    private static Guid ParseGuid(string value, string field) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid(field, $"{field} must be a non-empty UUID.");

    private static SourceKind? ParseSourceKind(string? value) =>
        value is null ? null : value switch
        {
            "web_page" => SourceKind.WebPage,
            "local_file" => SourceKind.LocalFile,
            "book" => SourceKind.Book,
            "podcast" => SourceKind.Podcast,
            "other" => SourceKind.Other,
            _ => throw Invalid("kind", "kind is invalid.")
        };

    private static SourceStatus? ParseSourceStatus(string? value) =>
        value is null ? null : value switch
        {
            "active" => SourceStatus.Active,
            "archived" => SourceStatus.Archived,
            _ => throw Invalid("status", "status is invalid.")
        };

    private static SourceLocatorType? ParseLocatorType(string? value) =>
        value is null ? null : value switch
        {
            "section" => SourceLocatorType.Section,
            "paragraph" => SourceLocatorType.Paragraph,
            "page" => SourceLocatorType.Page,
            "timestamp" => SourceLocatorType.Timestamp,
            _ => throw Invalid("locatorType", "locatorType is invalid.")
        };

    private static KnowledgeSearchMode ParseSearchMode(string value) =>
        value switch
        {
            "keyword" => KnowledgeSearchMode.Keyword,
            "fulltext" => KnowledgeSearchMode.Fulltext,
            _ => throw Invalid("mode", "mode must be keyword or fulltext.")
        };

    private static DraftStatus? ParseDraftStatus(string? value) =>
        value is null ? null : value switch
        {
            "draft" => DraftStatus.Draft,
            "pending_review" => DraftStatus.PendingReview,
            "conflict" => DraftStatus.Conflict,
            "applied" => DraftStatus.Applied,
            "rejected" => DraftStatus.Rejected,
            "discarded" => DraftStatus.Discarded,
            "expired" => DraftStatus.Expired,
            _ => throw Invalid("status", "status is invalid.")
        };

    private static DraftTargetObjectType? ParseTargetType(string? value) =>
        value is null ? null : value switch
        {
            "knowledge" => DraftTargetObjectType.Knowledge,
            "source" => DraftTargetObjectType.Source,
            "relation" => DraftTargetObjectType.Relation,
            _ => throw Invalid("targetObjectType", "targetObjectType is invalid.")
        };

    private static DraftOperationType? ParseOperationType(string? value) =>
        value is null ? null : value switch
        {
            "create" => DraftOperationType.Create,
            "update" => DraftOperationType.Update,
            "archive" => DraftOperationType.Archive,
            "restore" => DraftOperationType.Restore,
            "retract" => DraftOperationType.Retract,
            _ => throw Invalid("operationType", "operationType is invalid.")
        };

    private static McpToolRequestException Invalid(
        string field,
        string message) =>
        new("invalid_input", message);
}

public sealed record SourceReadResult(
    SourceDetail Source,
    SourceRevisionSummary Revision,
    SourceContentChunk Content);

public sealed record KnowledgeReadResult(
    KnowledgeRevisionDetail Knowledge,
    KnowledgeContentChunk Content);
