using CipherVault.Application.Documents;
using CipherVault.Application.Graph;
using CipherVault.Application.Search;
using CipherVault.Server.Api.Contracts;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeGraph;
using CipherVault.Core.Search;

namespace CipherVault.Server.Api.Endpoints;

public static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/tags",
                async (
                    int? pageSize,
                    string? cursor,
                    DocumentService documents,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await documents.ListTagsAsync(
                        new CursorPageRequest(pageSize, cursor),
                        cancellationToken)))
            .RequireAuthorization()
            .WithName("ListTags");

        endpoints.MapGet(
                "/api/search",
                async (
                    string? query,
                    string[]? tags,
                    string? format,
                    string? updatedAfter,
                    string? updatedBefore,
                    string? hasBacklinks,
                    string? orphanOnly,
                    int? pageSize,
                    string? cursor,
                    SearchService search,
                    CancellationToken cancellationToken) =>
                {
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        throw ApiQueryBinding.InvalidQuery(
                            "query is required.");
                    }

                    var request = new SearchQuery(
                        query,
                        tags,
                        ApiQueryBinding.ParseFormat(format),
                        ApiQueryBinding.ParseTimestamp(
                            updatedAfter,
                            nameof(updatedAfter)),
                        ApiQueryBinding.ParseTimestamp(
                            updatedBefore,
                            nameof(updatedBefore)),
                        ApiQueryBinding.ParseBoolean(
                            hasBacklinks,
                            nameof(hasBacklinks)),
                        ApiQueryBinding.ParseBoolean(
                            orphanOnly,
                            nameof(orphanOnly)) ?? false,
                        new CursorPageRequest(pageSize, cursor));
                    return Results.Ok(await search.SearchAsync(
                        request,
                        cancellationToken));
                })
            .RequireAuthorization()
            .WithName("SearchDocuments");

        endpoints.MapPost(
                "/api/search/query",
                async (
                    StructuredSearchApiRequest request,
                    SearchService search,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await search.SearchAsync(
                        request.ToApplicationQuery(),
                        cancellationToken)))
            .RequireAuthorization()
            .WithName("SearchDocumentsStructured");

        endpoints.MapGet(
                "/api/graph/local",
                async (
                    string? centerDocumentId,
                    int? depth,
                    string[]? tags,
                    string? folder,
                    int? maxNodes,
                    int? maxEdges,
                    GraphService graph,
                    CancellationToken cancellationToken) =>
                {
                    if (string.IsNullOrWhiteSpace(centerDocumentId))
                    {
                        throw ApiQueryBinding.InvalidQuery(
                            "centerDocumentId is required.");
                    }

                    var request = new LocalGraphQuery(
                        ApiQueryBinding.ParseGuid(
                            centerDocumentId,
                            nameof(centerDocumentId)),
                        depth ?? 2,
                        tags,
                        folder,
                        maxNodes ?? 200,
                        maxEdges ?? 400);
                    return Results.Ok(await graph.GetLocalGraphAsync(
                        request,
                        cancellationToken));
                })
            .RequireAuthorization()
            .WithName("GetLocalGraph");
        return endpoints;
    }
}
