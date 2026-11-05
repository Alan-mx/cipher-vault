using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Api.Contracts;

public sealed record KnowledgeSearchApiRequest(
    string? Query,
    string? Mode,
    Guid[]? TypeIds,
    Guid[]? SourceIds,
    string[]? Tags,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    int? PageSize,
    string? Cursor)
{
    public KnowledgeSearchQuery ToDomain() =>
        new(
            Query ?? string.Empty,
            ParseMode(Mode),
            TypeIds,
            SourceIds,
            Tags,
            UpdatedAfter,
            UpdatedBefore,
            PageSize ?? 20,
            Cursor);

    private static KnowledgeSearchMode ParseMode(string? value) =>
        value switch
        {
            null or "keyword" => KnowledgeSearchMode.Keyword,
            "fulltext" => KnowledgeSearchMode.Fulltext,
            _ => throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidSearchMode,
                "Search mode must be keyword or fulltext.")
        };
}
