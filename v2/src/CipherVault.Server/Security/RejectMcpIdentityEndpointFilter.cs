using CipherVault.Server.Api;

namespace CipherVault.Server.Security;

public sealed class RejectMcpIdentityEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var isMcp = context.HttpContext.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && identity.AuthenticationType
                == McpBearerAuthentication.Scheme);
        if (isMcp)
        {
            throw new ApiRequestException(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                "MCP cannot access this endpoint.");
        }
        return next(context);
    }
}
