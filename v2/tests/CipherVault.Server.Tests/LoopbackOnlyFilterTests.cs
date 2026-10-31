using System.Net;
using CipherVault.Server.Api;
using CipherVault.Server.Security;
using Microsoft.AspNetCore.Http;

namespace CipherVault.Server.Tests;

public sealed class LoopbackOnlyFilterTests
{
    [Fact]
    public async Task NonLoopbackRemoteIsRejected()
    {
        var context = new DefaultHttpContext
        {
            Connection =
            {
                RemoteIpAddress = IPAddress.Parse("192.0.2.10")
            }
        };
        var filter = new LoopbackOnlyFilter();
        var invocation = EndpointFilterInvocationContext.Create(context);

        var exception = await Assert.ThrowsAsync<ApiRequestException>(() =>
            filter.InvokeAsync(invocation, _ => ValueTask.FromResult<object?>(null)).AsTask());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (System.Net.HttpStatusCode)exception.StatusCode);
    }
}
