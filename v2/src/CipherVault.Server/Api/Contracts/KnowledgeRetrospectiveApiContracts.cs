using CipherVault.Application.KnowledgeBase;

namespace CipherVault.Server.Api.Contracts;

public sealed record CreateKnowledgeRetrospectiveDraftApiRequest(
    string? Topic,
    string[]? KnowledgeIds)
{
    public KnowledgeRetrospectiveCreateInput ToDomain()
    {
        var ids = new List<Guid>(KnowledgeIds?.Length ?? 0);
        foreach (var value in KnowledgeIds ?? [])
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                throw new ApiRequestException(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.InvalidInput,
                    "knowledgeIds must contain non-empty UUIDs.");
            }
            ids.Add(id);
        }
        return new KnowledgeRetrospectiveCreateInput(
            ids,
            Topic ?? string.Empty);
    }
}
