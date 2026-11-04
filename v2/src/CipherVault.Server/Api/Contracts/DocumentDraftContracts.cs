using System.Text.Json;
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
    int OperationCount);

public sealed record DocumentDraftDetailApiResponse(
    DocumentDraftApiItem Metadata,
    JsonElement Payload,
    DocumentDraftPreviewApiResponse Preview);

public sealed record DocumentDraftPageApiResponse(
    IReadOnlyList<DocumentDraftApiItem> Items,
    string? NextCursor);

public sealed record DocumentDraftMutationApiResponse(
    Guid DraftId,
    int DraftRevision,
    string Status,
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

    public static DocumentDraftDetailApiResponse ToApiResponse(
        this DocumentDraftDetail detail) =>
        new(
            ToApiItem(detail.Metadata),
            detail.Payload,
            new DocumentDraftPreviewApiResponse(
                detail.Preview.Content,
                detail.Preview.Tags,
                detail.Preview.UnifiedDiff,
                detail.Preview.DiffTruncated,
                detail.Preview.OperationCount));

    public static DocumentDraftMutationApiResponse ToApiResponse(
        this DocumentDraftMutationResult result) =>
        new(
            result.DraftId,
            result.DraftRevision,
            result.Status.ToDatabaseValue(),
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

    public static DocumentDraftType? ParseType(string? value) =>
        value switch
        {
            null => null,
            "create" => DocumentDraftType.Create,
            "update" => DocumentDraftType.Update,
            "trash" => DocumentDraftType.Trash,
            _ => throw InvalidRequest("draftType is invalid.")
        };

    private static DocumentDraftApiItem ToApiItem(
        DocumentDraftMetadata metadata) =>
        new(
            metadata.DraftId,
            metadata.DraftRevision,
            metadata.DraftType.ToDatabaseValue(),
            metadata.Status.ToDatabaseValue(),
            metadata.TargetDocumentId,
            metadata.BaseDocumentRevision,
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
            ApiErrorCodes.InvalidRequest,
            message);
}
