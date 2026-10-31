namespace CipherVault.Server.Api;

public static class RevisionETagFilterExtensions
{
    private const string ItemKey = "CipherVault.ExpectedRevision";

    public static RouteHandlerBuilder RequireRevisionETag(
        this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RevisionETagEndpointFilter>();

    public static int GetExpectedRevision(this HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var value)
            && value is int revision)
        {
            return revision;
        }

        throw new InvalidOperationException(
            "Revision ETag filter was not applied to this endpoint.");
    }

    internal static void SetExpectedRevision(
        HttpContext context,
        int revision) =>
        context.Items[ItemKey] = revision;
}

public sealed class RevisionETagEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var values = context.HttpContext.Request.Headers.IfMatch;
        if (values.Count != 1
            || !RevisionETag.TryParse(values[0], out var etag)
            || etag is null)
        {
            throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidIfMatch,
                "If-Match must contain one positive revision ETag.");
        }

        RevisionETagFilterExtensions.SetExpectedRevision(
            context.HttpContext,
            etag.Revision);
        return next(context);
    }
}
