using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Api.Contracts;

public sealed record KnowledgeTriageApiRequest(
    string? Reason,
    string? KnowledgeTypeId,
    int? StaleDays,
    string? Search,
    int? PageSize,
    string? Cursor)
{
    public KnowledgeTriageQuery ToDomain() =>
        new(
            ParseReason(Reason),
            ParseKnowledgeTypeId(KnowledgeTypeId),
            StaleDays ?? 30,
            Search,
            PageSize ?? 20,
            Cursor);

    private static KnowledgeTriageReason? ParseReason(string? value) =>
        value switch
        {
            null or "" => null,
            "missing_source" => KnowledgeTriageReason.MissingSource,
            "missing_required_property" =>
                KnowledgeTriageReason.MissingRequiredProperty,
            "stale" => KnowledgeTriageReason.Stale,
            _ => throw Invalid("reason")
        };

    private static Guid? ParseKnowledgeTypeId(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw Invalid("knowledgeTypeId");
    }

    private static ApiRequestException Invalid(string field) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            $"{field} is invalid.");
}
