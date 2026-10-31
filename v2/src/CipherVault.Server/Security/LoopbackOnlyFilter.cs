using CipherVault.Server.Api;

namespace CipherVault.Server.Security;

public sealed class LoopbackOnlyFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote))
        {
            throw new ApiRequestException(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                "Import is allowed only from a loopback client.");
        }

        return next(context);
    }
}
