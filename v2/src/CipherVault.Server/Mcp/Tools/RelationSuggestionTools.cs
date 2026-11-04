using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CipherVault.Application.Relations;
using CipherVault.Core.Documents;
using CipherVault.Core.Relations;
using CipherVault.Server.Api.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class RelationSuggestionTools(
    RelationSuggestionService suggestions,
    McpToolInvoker invoker)
{
    [McpServerTool(Name = McpToolNames.CreateRelationSuggestion)]
    [Description("Create an encrypted pending AI relation suggestion. MCP cannot apply or reject it.")]
    public Task<CallToolResult> CreateRelationSuggestion(
        string relationType,
        string sourceDocumentId,
        int sourceDocumentRevision,
        string targetDocumentId,
        int targetDocumentRevision,
        string reason,
        string idempotencyKey,
        string? modelName = null,
        string? agentName = null,
        string? clientName = null,
        JsonElement[]? evidence = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.CreateRelationSuggestion,
            async (context, token) =>
                await suggestions.CreateAsync(
                    new CreateRelationSuggestionRequest(
                        ParseRelationType(relationType),
                        ParseDocumentId(sourceDocumentId),
                        sourceDocumentRevision,
                        ParseDocumentId(targetDocumentId),
                        targetDocumentRevision,
                        reason,
                        modelName,
                        agentName,
                        clientName,
                        ParseEvidence(evidence)),
                    context.Actor,
                    ParseIdempotencyKey(idempotencyKey),
                    token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ListRelationSuggestions)]
    [Description("List relation suggestions with stable filtering and cursor pagination.")]
    public Task<CallToolResult> ListRelationSuggestions(
        string? status = null,
        string? relationType = null,
        string? sourceDocumentId = null,
        string? targetDocumentId = null,
        string? updatedAfter = null,
        string? updatedBefore = null,
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListRelationSuggestions,
            async (context, token) =>
                await suggestions.ListAsync(
                    new RelationSuggestionQuery(
                        ParseStatus(status),
                        relationType is null
                            ? null
                            : ParseRelationType(relationType),
                        sourceDocumentId is null
                            ? null
                            : ParseDocumentId(sourceDocumentId),
                        targetDocumentId is null
                            ? null
                            : ParseDocumentId(targetDocumentId),
                        ParseTimestamp(updatedAfter, "updatedAfter"),
                        ParseTimestamp(updatedBefore, "updatedBefore"),
                        new CursorPageRequest(pageSize, cursor)),
                    context.Actor,
                    token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ReadRelationSuggestion)]
    [Description("Read a relation suggestion with bounded evidence and document metadata.")]
    public Task<CallToolResult> ReadRelationSuggestion(
        string suggestionId,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ReadRelationSuggestion,
            async (context, token) =>
                await suggestions.GetAsync(
                    ParseSuggestionId(suggestionId),
                    context.Actor,
                    token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.UpdateRelationSuggestion)]
    [Description("Update a pending relation suggestion or rebuild a stale suggestion using current document revisions.")]
    public Task<CallToolResult> UpdateRelationSuggestion(
        string suggestionId,
        int expectedSuggestionRevision,
        string reason,
        string idempotencyKey,
        JsonElement[]? evidence = null,
        string? modelName = null,
        string? agentName = null,
        string? clientName = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.UpdateRelationSuggestion,
            async (context, token) =>
                await suggestions.UpdateAsync(
                    ParseSuggestionId(suggestionId),
                    expectedSuggestionRevision,
                    new UpdateRelationSuggestionRequest(
                        reason,
                        modelName,
                        agentName,
                        clientName,
                        ParseEvidence(evidence)),
                    context.Actor,
                    ParseIdempotencyKey(idempotencyKey),
                    token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.DiscardRelationSuggestion)]
    [Description("Discard a pending or stale relation suggestion. MCP cannot apply or reject suggestions.")]
    public Task<CallToolResult> DiscardRelationSuggestion(
        string suggestionId,
        int expectedSuggestionRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.DiscardRelationSuggestion,
            async (context, token) =>
                await suggestions.DiscardAsync(
                    ParseSuggestionId(suggestionId),
                    expectedSuggestionRevision,
                    context.Actor,
                    ParseIdempotencyKey(idempotencyKey),
                    token),
            cancellationToken);

    private static IReadOnlyList<RelationEvidence> ParseEvidence(
        JsonElement[]? evidence) =>
        evidence?
            .Select(RelationEvidenceApiRequest.Parse)
            .Select(item => item.ToDomain())
            .ToArray()
        ?? [];

    private static RelationType ParseRelationType(string value) =>
        value switch
        {
            "related" => RelationType.Related,
            "example_of" => RelationType.ExampleOf,
            "implements" => RelationType.Implements,
            "contrasts_with" => RelationType.ContrastsWith,
            "depends_on" => RelationType.DependsOn,
            _ => throw Invalid("relationType is invalid.")
        };

    private static RelationSuggestionStatus ParseStatus(string? value) =>
        value switch
        {
            null => RelationSuggestionStatus.PendingReview,
            "pending_review" => RelationSuggestionStatus.PendingReview,
            "stale" => RelationSuggestionStatus.Stale,
            "applied" => RelationSuggestionStatus.Applied,
            "rejected" => RelationSuggestionStatus.Rejected,
            "expired" => RelationSuggestionStatus.Expired,
            _ => throw Invalid("status is invalid.")
        };

    private static Guid ParseDocumentId(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid("documentId must be a non-empty UUID.");

    private static Guid ParseSuggestionId(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid("suggestionId must be a non-empty UUID.");

    private static Guid ParseIdempotencyKey(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new McpToolRequestException(
                "idempotency_key_required",
                "idempotencyKey must be a non-empty UUID.");

    private static DateTimeOffset? ParseTimestamp(
        string? value,
        string fieldName)
    {
        if (value is null)
        {
            return null;
        }
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : throw Invalid(
                $"{fieldName} must be an ISO 8601 timestamp.");
    }

    private static McpToolRequestException Invalid(string message) =>
        new("invalid_input", message);
}
