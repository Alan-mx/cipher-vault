using CipherVault.Application.KnowledgeBase;

namespace CipherVault.Server.Api.Contracts;

public sealed record CreateKnowledgeSynthesisDraftApiRequest(
    string? Topic,
    string? Question,
    string[]? KnowledgeIds,
    string? KnowledgeTypeId)
{
    public KnowledgeSynthesisCreateInput ToDomain()
    {
        var ids = ParseRequiredIds(KnowledgeIds, "knowledgeIds");
        var typeId = ParseRequiredId(KnowledgeTypeId, "knowledgeTypeId");
        return new KnowledgeSynthesisCreateInput(
            ids,
            Topic ?? string.Empty,
            Question,
            typeId);
    }

    private static IReadOnlyList<Guid> ParseRequiredIds(
        string[]? values,
        string field)
    {
        var ids = new List<Guid>(values?.Length ?? 0);
        foreach (var value in values ?? [])
        {
            ids.Add(ParseRequiredId(value, field));
        }
        return ids;
    }

    private static Guid ParseRequiredId(string? value, string field) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidInput,
                $"{field} must contain non-empty UUIDs.");
}
