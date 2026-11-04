using System.ComponentModel;
using System.Text.Json;
using CipherVault.Application.Drafts;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class DocumentDraftTools
{
    private readonly DocumentDraftService drafts;
    private readonly McpToolInvoker invoker;

    public DocumentDraftTools(
        DocumentDraftService drafts,
        McpToolInvoker invoker)
    {
        this.drafts = drafts;
        this.invoker = invoker;
    }

    [McpServerTool(Name = McpToolNames.ListDrafts)]
    [Description("List encrypted document drafts. Defaults to all statuses.")]
    public Task<CallToolResult> ListDrafts(
        string? status = null,
        string? type = null,
        string? targetDocumentId = null,
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListDrafts,
            async (_, token) => await drafts.ListAsync(
                new DocumentDraftQuery(
                    ParseStatus(status),
                    ParseType(type),
                    targetDocumentId is null
                        ? null
                        : ParseDocumentId(targetDocumentId),
                    new CursorPageRequest(pageSize, cursor)),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ReadDraft)]
    [Description("Read a document draft, its payload, and its computed preview.")]
    public Task<CallToolResult> ReadDraft(
        string draftId,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ReadDraft,
            async (_, token) => await drafts.GetAsync(
                ParseDraftId(draftId),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.CreateDraft)]
    [Description("Create an encrypted document draft. This does not modify formal documents.")]
    public Task<CallToolResult> CreateDraft(
        string draftType,
        JsonElement payload,
        string idempotencyKey,
        string? targetDocumentId = null,
        int? baseDocumentRevision = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.CreateDraft,
            async (context, token) => await drafts.CreateAsync(
                new CreateDocumentDraftRequest(
                    ParseRequiredType(draftType),
                    targetDocumentId is null
                        ? null
                        : ParseDocumentId(targetDocumentId),
                    baseDocumentRevision,
                    payload),
                context.Actor,
                ParseIdempotencyKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.UpdateDraft)]
    [Description("Update a document draft using expectedDraftRevision.")]
    public Task<CallToolResult> UpdateDraft(
        string draftId,
        int expectedDraftRevision,
        JsonElement payload,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.UpdateDraft,
            async (context, token) => await drafts.UpdateAsync(
                ParseDraftId(draftId),
                expectedDraftRevision,
                payload,
                context.Actor,
                ParseIdempotencyKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.RebaseDraft)]
    [Description("Rebase a conflicted draft onto a new formal document revision.")]
    public Task<CallToolResult> RebaseDraft(
        string draftId,
        int expectedDraftRevision,
        int observedDocumentRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.RebaseDraft,
            async (context, token) => await drafts.RebaseAsync(
                ParseDraftId(draftId),
                expectedDraftRevision,
                observedDocumentRevision,
                context.Actor,
                ParseIdempotencyKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.SubmitDraftForReview)]
    [Description("Move a draft into pending review. MCP cannot approve or apply drafts.")]
    public Task<CallToolResult> SubmitDraftForReview(
        string draftId,
        int expectedDraftRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.SubmitDraftForReview,
            async (context, token) => await drafts.SubmitReviewAsync(
                ParseDraftId(draftId),
                expectedDraftRevision,
                context.Actor,
                ParseIdempotencyKey(idempotencyKey),
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.DiscardDraft)]
    [Description("Discard a non-terminal document draft.")]
    public Task<CallToolResult> DiscardDraft(
        string draftId,
        int expectedDraftRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.DiscardDraft,
            async (context, token) => await drafts.DiscardAsync(
                ParseDraftId(draftId),
                expectedDraftRevision,
                context.Actor,
                ParseIdempotencyKey(idempotencyKey),
                token),
            cancellationToken);

    private static Guid ParseDraftId(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid("draftId must be a non-empty UUID.");

    private static Guid ParseDocumentId(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid("targetDocumentId must be a non-empty UUID.");

    private static Guid ParseIdempotencyKey(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new McpToolRequestException(
                "idempotency_key_required",
                "idempotencyKey must be a non-empty UUID.");

    private static DocumentDraftStatus? ParseStatus(string? value) =>
        value switch
        {
            null => null,
            "draft" => DocumentDraftStatus.Draft,
            "pending_review" => DocumentDraftStatus.PendingReview,
            "conflict" => DocumentDraftStatus.Conflict,
            "applied" => DocumentDraftStatus.Applied,
            "rejected" => DocumentDraftStatus.Rejected,
            "discarded" => DocumentDraftStatus.Discarded,
            "expired" => DocumentDraftStatus.Expired,
            _ => throw Invalid("status is invalid.")
        };

    private static DocumentDraftType? ParseType(string? value) =>
        value switch
        {
            null => null,
            "create" => DocumentDraftType.Create,
            "update" => DocumentDraftType.Update,
            "trash" => DocumentDraftType.Trash,
            _ => throw Invalid("type is invalid.")
        };

    private static DocumentDraftType ParseRequiredType(string value) =>
        ParseType(value)
        ?? throw Invalid("draftType is required.");

    private static McpToolRequestException Invalid(string message) =>
        new("invalid_input", message);
}
