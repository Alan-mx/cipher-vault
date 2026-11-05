using System.ComponentModel;
using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.KnowledgeBase;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class UnifiedDraftTools(
    UnifiedDraftService drafts,
    McpToolInvoker invoker)
{
    [McpServerTool(Name = McpToolNames.CreateDraft)]
    [Description("Create a unified Draft. MCP cannot apply or reject drafts.")]
    public Task<CallToolResult> CreateDraft(
        string targetObjectType,
        string operationType,
        JsonElement payload,
        string idempotencyKey,
        string? targetId = null,
        int? baseRevision = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.CreateDraft,
            async (context, token) => await drafts.CreateAsync(
                new CreateDraftInput(
                    ParseTarget(targetObjectType),
                    ParseOperation(operationType),
                    targetId is null ? null : ParseGuid(targetId, "targetId"),
                    baseRevision,
                    payload),
                context.Actor,
                ParseKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.UpdateDraft)]
    [Description("Replace a Draft payload with expectedDraftRevision.")]
    public Task<CallToolResult> UpdateDraft(
        string draftId,
        int expectedDraftRevision,
        JsonElement payload,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.UpdateDraft,
            async (context, token) => await drafts.UpdateAsync(
                ParseGuid(draftId, "draftId"),
                expectedDraftRevision,
                payload,
                context.Actor,
                ParseKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.RebaseDraft)]
    [Description("Rebase a Draft against the latest target revision and Knowledge Model.")]
    public Task<CallToolResult> RebaseDraft(
        string draftId,
        int expectedDraftRevision,
        int observedTargetRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.RebaseDraft,
            async (context, token) => await drafts.RebaseAsync(
                ParseGuid(draftId, "draftId"),
                expectedDraftRevision,
                observedTargetRevision,
                context.Actor,
                ParseKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.SubmitDraftForReview)]
    [Description("Submit a Draft for Web user review.")]
    public Task<CallToolResult> SubmitDraftForReview(
        string draftId,
        int expectedDraftRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.SubmitDraftForReview,
            async (context, token) => await drafts.SubmitAsync(
                ParseGuid(draftId, "draftId"),
                expectedDraftRevision,
                context.Actor,
                ParseKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.DiscardDraft)]
    [Description("Discard a non-terminal Draft.")]
    public Task<CallToolResult> DiscardDraft(
        string draftId,
        int expectedDraftRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.DiscardDraft,
            async (context, token) => await drafts.DiscardAsync(
                ParseGuid(draftId, "draftId"),
                expectedDraftRevision,
                context.Actor,
                ParseKey(idempotencyKey),
                token),
            cancellationToken);

    private static DraftTargetObjectType ParseTarget(string value) =>
        value switch
        {
            "knowledge" => DraftTargetObjectType.Knowledge,
            "source" => DraftTargetObjectType.Source,
            "relation" => DraftTargetObjectType.Relation,
            _ => throw Invalid("targetObjectType is invalid.")
        };

    private static DraftOperationType ParseOperation(string value) =>
        value switch
        {
            "create" => DraftOperationType.Create,
            "update" => DraftOperationType.Update,
            "archive" => DraftOperationType.Archive,
            "restore" => DraftOperationType.Restore,
            "retract" => DraftOperationType.Retract,
            _ => throw Invalid("operationType is invalid.")
        };

    private static Guid ParseGuid(string value, string field) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid($"{field} must be a non-empty UUID.");

    private static Guid ParseKey(string value) =>
        ParseGuid(value, "idempotencyKey");

    private static McpToolRequestException Invalid(string message) =>
        new("invalid_input", message);
}
