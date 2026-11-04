using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;

namespace CipherVault.Server.Api.Contracts;

public sealed record CreateDocumentDraftApiRequest(
    string DraftType,
    Guid? TargetDocumentId,
    int? BaseDocumentRevision,
    JsonElement Payload);

public sealed record UpdateDocumentDraftApiRequest(
    int ExpectedDraftRevision,
    JsonElement Payload);

public sealed record DocumentDraftRevisionApiRequest(
    int ExpectedDraftRevision);

public sealed record RebaseDocumentDraftApiRequest(
    int ExpectedDraftRevision,
    int ObservedDocumentRevision);

public sealed record DocumentDraftApiItem(
    Guid DraftId,
    int DraftRevision,
    string DraftType,
    string Status,
    Guid? TargetDocumentId,
    int? BaseDocumentRevision,
    Guid? FutureDocumentId,
    string SourceActor,
    string? Title,
    string? VirtualPath,
    string? Format,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TerminalAt,
    Guid? AppliedDocumentId,
    int? AppliedDocumentRevision);

public sealed record DocumentDraftPreviewApiResponse(
    string Content,
    IReadOnlyList<string> Tags,
    string UnifiedDiff,
    bool DiffTruncated,
    int OperationCount,
    DocumentDraftStructuredChangesApiResponse StructuredChanges,
    DocumentDraftOperationSummaryApiResponse OperationSummary,
    DocumentDraftBodyEditApiResponse BodyEdit);

public sealed record DocumentDraftDetailApiResponse(
    DocumentDraftApiItem Metadata,
    JsonElement Payload,
    DocumentDraftReviewContextApiResponse? ReviewContext,
    DocumentDraftPreviewApiResponse Preview);

public sealed record DocumentDraftReviewContextApiResponse(
    string? Summary,
    IReadOnlyList<DocumentDraftReviewSourceApiResponse> Sources);

public sealed record DocumentDraftReviewSourceApiResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? DocumentId,
    string? Title,
    string? VirtualPath,
    string? Reference,
    string Excerpt);

public sealed record DocumentDraftFieldChangeApiResponse(
    string FieldKind,
    string? Key,
    bool BeforeExists,
    string? BeforeValueType,
    string? Before,
    bool BeforeTruncated,
    bool AfterExists,
    string? AfterValueType,
    string? After,
    bool AfterTruncated);

public sealed record DocumentDraftStructuredChangesApiResponse(
    IReadOnlyList<DocumentDraftFieldChangeApiResponse> Changes,
    int TotalChangeCount,
    bool Truncated);

public sealed record DocumentDraftOperationCountApiResponse(
    string Type,
    int Count);

public sealed record DocumentDraftOperationSummaryApiResponse(
    int Total,
    IReadOnlyList<DocumentDraftOperationCountApiResponse> Items);

public sealed record DocumentDraftBodyEditApiResponse(
    bool CanSave,
    string? Reason);

public sealed record DocumentDraftPageApiResponse(
    IReadOnlyList<DocumentDraftApiItem> Items,
    string? NextCursor);

public sealed record DocumentDraftSummaryApiResponse(
    int PendingReview,
    int Working,
    int History);

public sealed record DocumentDraftMutationApiResponse(
    Guid DraftId,
    int DraftRevision,
    string Status,
    Guid? FutureDocumentId,
    DateTimeOffset UpdatedAt);

public sealed record DocumentDraftApplyApiResponse(
    Guid DraftId,
    int DraftRevision,
    string Status,
    DocumentWriteResult DocumentWriteResult);

public static class DocumentDraftApiMapper
{
    public static CreateDocumentDraftRequest ToApplicationRequest(
        this CreateDocumentDraftApiRequest request) =>
        new(
            ParseType(request.DraftType)
                ?? throw InvalidRequest("draftType is required."),
            request.TargetDocumentId,
            request.BaseDocumentRevision,
            RequirePayload(request.Payload));

    public static DocumentDraftPageApiResponse ToApiResponse(
        this DocumentDraftPage page) =>
        new(
            page.Items.Select(ToApiItem).ToArray(),
            page.NextCursor);

    public static DocumentDraftSummaryApiResponse ToApiResponse(
        this DocumentDraftQueueSummary summary) =>
        new(summary.PendingReview, summary.Working, summary.History);

    public static DocumentDraftDetailApiResponse ToApiResponse(
        this DocumentDraftDetail detail) =>
        new(
            ToApiItem(detail.Metadata),
            detail.Payload,
            ToApiResponse(detail.ReviewContext),
            new DocumentDraftPreviewApiResponse(
                detail.Preview.Content,
                detail.Preview.Tags,
                detail.Preview.UnifiedDiff,
                detail.Preview.DiffTruncated,
                detail.Preview.OperationCount,
                ToApiResponse(detail.Preview.StructuredChanges),
                ToApiResponse(detail.Preview.OperationSummary),
                ToApiResponse(detail.Preview.BodyEdit)));

    public static DocumentDraftMutationApiResponse ToApiResponse(
        this DocumentDraftMutationResult result) =>
        new(
            result.DraftId,
            result.DraftRevision,
            result.Status.ToDatabaseValue(),
            result.FutureDocumentId,
            result.UpdatedAt);

    public static DocumentDraftApplyApiResponse ToApiResponse(
        this DocumentDraftApplyResult result) =>
        new(
            result.DraftId,
            result.DraftRevision,
            result.Status.ToDatabaseValue(),
            result.DocumentWriteResult);

    public static DocumentDraftStatus? ParseStatus(string? value) =>
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
            _ => throw InvalidRequest("status is invalid.")
        };

    public static DocumentDraftQueue ParseQueue(string? value) =>
        value switch
        {
            null => DocumentDraftQueue.PendingReview,
            "pending_review" => DocumentDraftQueue.PendingReview,
            "working" => DocumentDraftQueue.Working,
            "history" => DocumentDraftQueue.History,
            _ => throw InvalidRequest("queue is invalid.")
        };

    public static DocumentDraftType? ParseType(string? value) =>
        value switch
        {
            null => null,
            "create" => DocumentDraftType.Create,
            "update" => DocumentDraftType.Update,
            "trash" => DocumentDraftType.Trash,
            _ => throw InvalidRequest("draftType is invalid.")
        };

    public static VaultActorKind? ParseDraftSourceActor(string? value) =>
        value switch
        {
            null => null,
            "user_ui" => VaultActorKind.UserUi,
            "mcp" => VaultActorKind.Mcp,
            _ => throw InvalidRequest("sourceActor is invalid.")
        };

    public static DateTimeOffset? ParseTimestamp(
        string? value,
        string fieldName)
    {
        if (value is null)
        {
            return null;
        }
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw InvalidRequest(
                $"{fieldName} must be an ISO 8601 timestamp.");
        }
        return parsed.ToUniversalTime();
    }

    public static int? ParsePageSize(string? value)
    {
        if (value is null)
        {
            return null;
        }
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var pageSize)
            || pageSize is < 1 or > 200)
        {
            throw InvalidRequest("pageSize must be between 1 and 200.");
        }
        return pageSize;
    }

    private static DocumentDraftApiItem ToApiItem(
        DocumentDraftMetadata metadata) =>
        new(
            metadata.DraftId,
            metadata.DraftRevision,
            metadata.DraftType.ToDatabaseValue(),
            metadata.Status.ToDatabaseValue(),
            metadata.TargetDocumentId,
            metadata.BaseDocumentRevision,
            metadata.FutureDocumentId,
            metadata.SourceActor,
            metadata.Title,
            metadata.VirtualPath,
            metadata.Format is null
                ? null
                : DocumentDraftPayloadEngine.FormatToDatabaseValue(
                    metadata.Format.Value),
            metadata.CreatedAt,
            metadata.UpdatedAt,
            metadata.TerminalAt,
            metadata.AppliedDocumentId,
            metadata.AppliedDocumentRevision);

    private static DocumentDraftReviewContextApiResponse? ToApiResponse(
        DocumentDraftReviewContext? context) =>
        context is null
            ? null
            : new DocumentDraftReviewContextApiResponse(
                context.Summary,
                context.Sources.Select(source =>
                    new DocumentDraftReviewSourceApiResponse(
                        source.DocumentId,
                        source.Title,
                        source.VirtualPath,
                        source.Reference,
                        source.Excerpt)).ToArray());

    private static DocumentDraftStructuredChangesApiResponse ToApiResponse(
        DocumentDraftStructuredChanges changes) =>
        new(
            changes.Changes.Select(change =>
                new DocumentDraftFieldChangeApiResponse(
                    change.FieldKind,
                    change.Key,
                    change.BeforeExists,
                    change.BeforeValueType,
                    change.Before,
                    change.BeforeTruncated,
                    change.AfterExists,
                    change.AfterValueType,
                    change.After,
                    change.AfterTruncated)).ToArray(),
            changes.TotalChangeCount,
            changes.Truncated);

    private static DocumentDraftOperationSummaryApiResponse ToApiResponse(
        DocumentDraftOperationSummary summary) =>
        new(
            summary.Total,
            summary.Items.Select(item =>
                new DocumentDraftOperationCountApiResponse(
                    item.Type,
                    item.Count)).ToArray());

    private static DocumentDraftBodyEditApiResponse ToApiResponse(
        DocumentDraftBodyEditCapability bodyEdit) =>
        new(bodyEdit.CanSave, bodyEdit.Reason);

    private static JsonElement RequirePayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            throw InvalidRequest("payload is required.");
        }
        return payload.Clone();
    }

    private static ApiRequestException InvalidRequest(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            message);
}
