using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Search;
using CipherVault.Core.Semantic;

namespace CipherVault.Server.Api.Contracts;

public sealed record SearchPropertyFilterApiRequest(
    string Field,
    string Operator,
    JsonElement Value);

public sealed record StructuredSearchApiRequest(
    string? Query,
    string? SearchMode,
    string[]? Tags,
    string? Format,
    DateTimeOffset? UpdatedAfter,
    DateTimeOffset? UpdatedBefore,
    bool? HasBacklinks,
    bool? OrphanOnly,
    string? PropertyMatch,
    IReadOnlyList<SearchPropertyFilterApiRequest>? PropertyFilters,
    int? PageSize,
    string? Cursor);

public static class StructuredSearchApiMapper
{
    public static SearchQuery ToApplicationQuery(
        this StructuredSearchApiRequest request) =>
        new(
            request.Query ?? string.Empty,
            ParseSearchMode(request.SearchMode),
            request.Tags,
            ApiQueryBinding.ParseFormat(request.Format),
            request.UpdatedAfter,
            request.UpdatedBefore,
            request.HasBacklinks,
            request.OrphanOnly ?? false,
            new CursorPageRequest(request.PageSize, request.Cursor),
            request.PropertyFilters?
                .Select(filter => filter.ToApplicationFilter())
                .ToArray(),
            ParseMatch(request.PropertyMatch));

    public static SearchMode ParseSearchMode(string? value) =>
        value switch
        {
            null or "keyword" => SearchMode.Keyword,
            "semantic" => SearchMode.Semantic,
            "hybrid" => SearchMode.Hybrid,
            _ => throw InvalidRequest("searchMode is invalid.")
        };

    private static SearchPropertyFilter ToApplicationFilter(
        this SearchPropertyFilterApiRequest filter)
    {
        if (filter.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw InvalidRequest("Property filter value is required.");
        }
        return new SearchPropertyFilter(
            filter.Field,
            ParseOperator(filter.Operator),
            filter.Value.Clone());
    }

    private static SearchPropertyOperator ParseOperator(string value) =>
        value switch
        {
            "exists" => SearchPropertyOperator.Exists,
            "eq" => SearchPropertyOperator.Eq,
            "contains" => SearchPropertyOperator.Contains,
            "in" => SearchPropertyOperator.In,
            "gte" => SearchPropertyOperator.Gte,
            "lte" => SearchPropertyOperator.Lte,
            _ => throw InvalidRequest("Property operator is invalid.")
        };

    private static SearchPropertyMatch ParseMatch(string? value) =>
        value switch
        {
            null or "all" => SearchPropertyMatch.All,
            "any" => SearchPropertyMatch.Any,
            _ => throw InvalidRequest("propertyMatch must be all or any.")
        };

    private static ApiRequestException InvalidRequest(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            message);
}
