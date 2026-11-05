using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Api.Endpoints;

public static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/sources")
            .RequireAuthorization();
        group.MapGet(
                "",
                async (
                    string? status,
                    string? kind,
                    string? query,
                    int? pageSize,
                    string? cursor,
                    SourceService sources,
                    CancellationToken cancellationToken) =>
                {
                    var result = await sources.ListAsync(
                        new SourceQuery(
                            ParseStatus(status),
                            ParseKind(kind),
                            query,
                            pageSize ?? 20,
                            cursor),
                        cancellationToken);
                    return Results.Ok(result);
                })
            .WithName("ListSources");

        group.MapGet(
                "/{sourceId}",
                async (
                    string sourceId,
                    SourceService sources,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var detail = await sources.GetAsync(
                        ApiQueryBinding.ParseGuid(sourceId, "sourceId"),
                        cancellationToken);
                    context.Response.SetRevisionETag(detail.CurrentRevision);
                    return Results.Ok(detail);
                })
            .WithName("GetSource");

        group.MapGet(
                "/{sourceId}/revisions",
                async (
                    string sourceId,
                    int? pageSize,
                    string? cursor,
                    SourceService sources,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await sources.ListRevisionsAsync(
                        ApiQueryBinding.ParseGuid(sourceId, "sourceId"),
                        pageSize ?? 20,
                        cursor,
                        cancellationToken)))
            .WithName("ListSourceRevisions");

        group.MapGet(
                "/{sourceId}/revisions/{revision}/content",
                async (
                    string sourceId,
                    int revision,
                    int? offset,
                    int? maxChars,
                    SourceService sources,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    var chunk = await sources.ReadContentAsync(
                        ApiQueryBinding.ParseGuid(sourceId, "sourceId"),
                        revision,
                        offset ?? 0,
                        maxChars ?? 32_768,
                        cancellationToken);
                    context.Response.SetRevisionETag(chunk.SourceRevision);
                    return Results.Ok(chunk);
                })
            .WithName("ReadSourceContent");

        group.MapGet(
                "/{sourceId}/revisions/{revision}/locators",
                async (
                    string sourceId,
                    int revision,
                    string? locatorType,
                    int? pageSize,
                    string? cursor,
                    SourceService sources,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await sources.ListLocatorsAsync(
                        ApiQueryBinding.ParseGuid(sourceId, "sourceId"),
                        revision,
                        ParseLocatorType(locatorType),
                        pageSize ?? 20,
                        cursor,
                        cancellationToken)))
            .WithName("ListSourceLocators");

        group.MapGet(
                "/{sourceId}/revisions/{revision}/locators/{locatorId}/content",
                async (
                    string sourceId,
                    int revision,
                    string locatorId,
                    int? offset,
                    int? maxChars,
                    SourceService sources,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await sources.ReadLocatorAsync(
                        ApiQueryBinding.ParseGuid(sourceId, "sourceId"),
                        revision,
                        ApiQueryBinding.ParseGuid(locatorId, "locatorId"),
                        offset ?? 0,
                        maxChars ?? 32_768,
                        cancellationToken)))
            .WithName("ReadSourceLocator");
        return endpoints;
    }

    private static SourceStatus? ParseStatus(string? value) =>
        value switch
        {
            null => null,
            "active" => SourceStatus.Active,
            "archived" => SourceStatus.Archived,
            _ => throw Invalid("status")
        };

    private static SourceKind? ParseKind(string? value) =>
        value switch
        {
            null => null,
            "web_page" => SourceKind.WebPage,
            "local_file" => SourceKind.LocalFile,
            "book" => SourceKind.Book,
            "podcast" => SourceKind.Podcast,
            "other" => SourceKind.Other,
            _ => throw Invalid("kind")
        };

    private static SourceLocatorType? ParseLocatorType(string? value) =>
        value switch
        {
            null => null,
            "section" => SourceLocatorType.Section,
            "paragraph" => SourceLocatorType.Paragraph,
            "page" => SourceLocatorType.Page,
            "timestamp" => SourceLocatorType.Timestamp,
            _ => throw Invalid("locatorType")
        };

    private static ApiRequestException Invalid(string field) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            $"{field} is invalid.");
}
