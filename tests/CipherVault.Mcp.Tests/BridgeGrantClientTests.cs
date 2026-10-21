using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CipherVault.Mcp.Tests;

public sealed class BridgeGrantClientTests
{
    [Fact]
    public async Task ToolCallRequestsGrantAndRetriesWithSecret()
    {
        var grantId = Guid.NewGuid();
        var handler = new GrantSequenceHandler(grantId);
        using var http = new HttpClient(handler);
        var settings = new BridgeSettings(
            new Uri("http://127.0.0.1:43127/mcp"),
            "agent-token",
            Guid.NewGuid(),
            new HashSet<string>(StringComparer.Ordinal) { "read" });
        var bridge = new HostMcpBridgeClient(settings, http);

        var result = await bridge.CallToolAsync(
            "list_catalog",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, handler.GrantRequestCount);
        Assert.Equal(2, handler.ToolCallCount);
        Assert.True(handler.SecondToolCallHadGrant);
    }

    [Fact]
    public async Task RevokeGrantClearsCachedSecret()
    {
        var grantId = Guid.NewGuid();
        var handler = new GrantSequenceHandler(grantId);
        using var http = new HttpClient(handler);
        var settings = new BridgeSettings(
            new Uri("http://127.0.0.1:43127/mcp"),
            "agent-token",
            Guid.NewGuid(),
            new HashSet<string>(StringComparer.Ordinal) { "read" });
        var bridge = new HostMcpBridgeClient(settings, http);
        _ = await bridge.CallToolAsync(
            "list_catalog",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        await bridge.RevokeGrantAsync();

        Assert.Equal(1, handler.RevokeCount);
    }

    private sealed class GrantSequenceHandler(Guid grantId) : HttpMessageHandler
    {
        public int GrantRequestCount { get; private set; }
        public int ToolCallCount { get; private set; }
        public int RevokeCount { get; private set; }
        public bool SecondToolCallHadGrant { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/mcp/access-grants")
            {
                GrantRequestCount++;
                return JsonResponse(
                    $$"""
                    {
                      "ok": true,
                      "data": {
                        "grant": {
                          "grantId": "{{grantId:D}}"
                        },
                        "secret": "grant-secret"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Delete &&
                request.RequestUri?.AbsolutePath.StartsWith(
                    "/mcp/access-grants/",
                    StringComparison.Ordinal) == true)
            {
                RevokeCount++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/mcp")
            {
                ToolCallCount++;
                var hasGrant = request.Headers.TryGetValues(
                    "X-CipherVault-Grant",
                    out var values) &&
                    values.SingleOrDefault() == "grant-secret";
                if (ToolCallCount == 2)
                {
                    SecondToolCallHadGrant = hasGrant;
                }
                var text = hasGrant
                    ? """{"ok":true,"page":{"items":[]}}"""
                    : """{"ok":false,"error":{"code":"access_grant_required","message":"grant required"}}""";
                return JsonResponse(
                    JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = "1",
                        result = new
                        {
                            content = new[]
                            {
                                new { type = "text", text }
                            }
                        }
                    }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
