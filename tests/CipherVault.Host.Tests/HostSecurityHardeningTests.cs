using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Text;
using System.Text.Json;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class HostSecurityHardeningTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RepeatedAuthenticationFailuresAreRateLimitedAndLogged()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await SendHealthAsync(
                fixture.Client,
                adminToken: "wrong-token");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var blocked = await SendHealthAsync(
            fixture.Client,
            adminToken: "wrong-token");
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        var payload = await blocked.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("rate_limited", payload.Error.Code);

        var log = await ReadSecurityLogAsync(fixture.SecurityLogDirectory);
        Assert.Contains("\"invalid_token\"", log, StringComparison.Ordinal);
        Assert.Contains("\"rate_limited\"", log, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-token", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequestIsRejectedBeforeAuthentication()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/health")
        {
            Content = new StringContent(
                new string('x', 8_192),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Host = "127.0.0.1:43127";

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)413, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("request_too_large", payload.Error.Code);
    }

    [Fact]
    public async Task AgentSuccessDoesNotResetAdminAuthenticationRateLimit()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var agentToken = await CreateAgentAsync(fixture.Client);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var invalid = await SendHealthAsync(
                fixture.Client,
                adminToken: "wrong-token");
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        }

        using (var validAgentRequest = CreateMcpRequest(agentToken))
        using (var validAgent = await fixture.Client.SendAsync(validAgentRequest))
        {
            Assert.NotEqual(HttpStatusCode.Unauthorized, validAgent.StatusCode);
        }

        using (var fifthFailure = await SendHealthAsync(
                   fixture.Client,
                   adminToken: "wrong-token"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, fifthFailure.StatusCode);
        }
        using var blocked = await SendHealthAsync(
            fixture.Client,
            adminToken: "wrong-token");
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
    }

    [Fact]
    public async Task AgentTokenIsRevokedImmediatelyAndCannotUseAdminApi()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var token = await CreateAgentAsync(fixture.Client);

        using var mcpRequest = CreateMcpRequest(token);
        using var accepted = await fixture.Client.SendAsync(mcpRequest);
        Assert.NotEqual(HttpStatusCode.Unauthorized, accepted.StatusCode);

        using var adminRequest = HostTestFixture.CreateRequest();
        adminRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token);
        using var adminResponse = await fixture.Client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, adminResponse.StatusCode);

        using var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            "/admin/v1/clients/security-agent");
        deleteRequest.Headers.Host = "127.0.0.1:43127";
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var deleted = await fixture.Client.SendAsync(deleteRequest);
        deleted.EnsureSuccessStatusCode();
        var log = await ReadSecurityLogAsync(fixture.SecurityLogDirectory);
        Assert.Contains("\"token_revoked\"", log, StringComparison.Ordinal);
        Assert.Contains("\"security-agent\"", log, StringComparison.Ordinal);

        using var revokedRequest = CreateMcpRequest(token);
        using var revoked = await fixture.Client.SendAsync(revokedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task InvalidOriginAndHostAreRejectedAndLogged()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var originRequest = HostTestFixture.CreateRequest();
        originRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        originRequest.Headers.TryAddWithoutValidation(
            "Origin",
            "https://evil.example");
        using var originResponse = await fixture.Client.SendAsync(originRequest);
        Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);

        using var hostRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/health");
        hostRequest.Headers.Host = "localhost:43127";
        using var hostResponse = await fixture.Client.SendAsync(hostRequest);
        Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);

        var log = await ReadSecurityLogAsync(fixture.SecurityLogDirectory);
        Assert.Contains("\"invalid_origin\"", log, StringComparison.Ordinal);
        Assert.Contains("\"invalid_host\"", log, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendHealthAsync(
        HttpClient client,
        string adminToken)
    {
        using var request = HostTestFixture.CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            adminToken);
        return await client.SendAsync(request);
    }

    private static async Task<string> CreateAgentAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/clients")
        {
            Content = JsonContent.Create(
                new HostCreateClientRequest(
                    "security-agent",
                    "readOnly"))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data.Token;
    }

    private static HttpRequestMessage CreateMcpRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static async Task<string> ReadSecurityLogAsync(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }
        var files = Directory.GetFiles(directory, "security-*.jsonl");
        var contents = new List<string>();
        foreach (var file in files)
        {
            contents.Add(await File.ReadAllTextAsync(file));
        }
        return string.Join(Environment.NewLine, contents);
    }
}
