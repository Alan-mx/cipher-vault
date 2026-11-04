using System.Text.Json;
using System.Globalization;
using CipherVault.Core.Relations;

namespace CipherVault.Server.Api.Contracts;

public sealed record RelationEvidenceApiRequest(
    string DocumentId,
    int DocumentRevision,
    string[]? HeadingPath,
    string Excerpt)
{
    public RelationEvidence ToDomain() =>
        new(
            Guid.TryParse(DocumentId, out var id)
                ? id
                : throw Invalid("documentId is invalid."),
            DocumentRevision,
            HeadingPath ?? [],
            Excerpt);

    public static RelationEvidenceApiRequest Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Any(property =>
                property.Name is not (
                    "documentId"
                    or "documentRevision"
                    or "headingPath"
                    or "excerpt")))
        {
            throw Invalid("Relation evidence contains unsupported fields.");
        }
        try
        {
            return JsonSerializer.Deserialize<RelationEvidenceApiRequest>(
                value.GetRawText(),
                ApiJson.CreateOptions())
                ?? throw Invalid("Relation evidence is invalid.");
        }
        catch (JsonException)
        {
            throw Invalid("Relation evidence is invalid.");
        }
    }

    private static ApiRequestException Invalid(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            message);
}

public sealed record RelationSuggestionListApiResponse(
    IReadOnlyList<RelationSuggestionListItem> Items,
    string? NextCursor);

public sealed record RelationSuggestionSummaryApiResponse(
    int PendingReview,
    int Stale,
    int Applied,
    int Rejected,
    int Expired);

public sealed record RelationSuggestionRevisionApiRequest(
    int ExpectedSuggestionRevision);

public sealed record RelationSuggestionApplyApiResponse(
    Guid SuggestionId,
    int SuggestionRevision,
    string Status,
    Guid? RelationId,
    string? ApplyResult,
    DateTimeOffset? AppliedAt);

public sealed record RelationSuggestionRejectApiResponse(
    Guid SuggestionId,
    int SuggestionRevision,
    string Status,
    DateTimeOffset RejectedAt);

public sealed record DocumentRelationRetractApiResponse(
    Guid RelationId,
    string Status,
    DateTimeOffset RetractedAt,
    Guid AuditEventId);

public static class RelationSuggestionApiMapper
{
    public static RelationSuggestionStatus ParseStatus(string? value) =>
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

    public static RelationType? ParseRelationType(string? value) =>
        value switch
        {
            null => null,
            "related" => RelationType.Related,
            "example_of" => RelationType.ExampleOf,
            "implements" => RelationType.Implements,
            "contrasts_with" => RelationType.ContrastsWith,
            "depends_on" => RelationType.DependsOn,
            _ => throw Invalid("relationType is invalid.")
        };

    public static int ParsePageSize(int? value)
    {
        var pageSize = value ?? 50;
        return pageSize is < 1 or > 200
            ? throw Invalid("pageSize must be between 1 and 200.")
            : pageSize;
    }

    public static int ParseExpectedRevision(int value) =>
        value >= 1
            ? value
            : throw Invalid(
                "expectedSuggestionRevision must be at least 1.");

    public static Guid ParseGuid(string value, string fieldName) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid($"{fieldName} must be a non-empty UUID.");

    public static DateTimeOffset? ParseTimestamp(
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

    public static RelationSuggestionListApiResponse ToApiResponse(
        this RelationSuggestionPage page) =>
        new(page.Items, page.NextCursor);

    public static RelationSuggestionSummaryApiResponse ToApiResponse(
        this RelationSuggestionSummary summary) =>
        new(
            summary.PendingReview,
            summary.Stale,
            summary.Applied,
            summary.Rejected,
            summary.Expired);

    public static RelationSuggestionApplyApiResponse ToApiResponse(
        this RelationSuggestionApplyOutcome outcome) =>
        new(
            outcome.SuggestionId,
            outcome.SuggestionRevision,
            outcome.Status.ToDatabaseValue(),
            outcome.RelationId,
            outcome.ApplyResult.ToDatabaseValue(),
            outcome.AppliedAt);

    public static RelationSuggestionRejectApiResponse ToApiResponse(
        this RelationSuggestionMutationResult result) =>
        new(
            result.SuggestionId,
            result.SuggestionRevision,
            result.Status.ToDatabaseValue(),
            result.UpdatedAt);

    public static DocumentRelationRetractApiResponse ToApiResponse(
        this DocumentRelationRetractResult result) =>
        new(
            result.RelationId,
            result.Status.ToDatabaseValue(),
            result.RetractedAt,
            result.AuditEventId);

    private static ApiRequestException Invalid(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            message);
}
