using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class AdminAccessGrantEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AdminCanListAndRevokeGrant()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateAgentAsync(fixture.Client);
        var issued = await CreateGrantAsync(fixture.Client, token);

        using var list = await SendAdminRequestAsync(
            fixture.Client,
            HttpMethod.Get,
            "/admin/v1/access-grants");
        list.EnsureSuccessStatusCode();
        var listBody = await list.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<
            HostApiResponse<IReadOnlyList<HostAccessGrantData>>>(
            listBody,
            JsonOptions);
        Assert.NotNull(payload);
        var grant = Assert.Single(payload.Data);
        Assert.Equal(issued.Grant.GrantId, grant.GrantId);
        Assert.Equal("active", grant.State);
        Assert.DoesNotContain("secret", listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", listBody, StringComparison.OrdinalIgnoreCase);

        using var revoke = await SendAdminRequestAsync(
            fixture.Client,
            HttpMethod.Delete,
            $"/admin/v1/access-grants/{issued.Grant.GrantId:D}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var empty = await SendAdminRequestAsync(
            fixture.Client,
            HttpMethod.Get,
            "/admin/v1/access-grants");
        var emptyPayload = await empty.Content.ReadFromJsonAsync<
            HostApiResponse<IReadOnlyList<HostAccessGrantData>>>(JsonOptions);
        Assert.NotNull(emptyPayload);
        Assert.Empty(emptyPayload.Data);

        using var missing = await SendAdminRequestAsync(
            fixture.Client,
            HttpMethod.Delete,
            $"/admin/v1/access-grants/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task PendingGrantAppearsInAdminList()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateAgentAsync(fixture.Client);
        var promptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<HostAccessGrantPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AccessGrantPrompt.Handler = async (_, _) =>
        {
            promptStarted.TrySetResult();
            return await approval.Task;
        };

        var pendingRequest = SendGrantRequestAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read");
        await promptStarted.Task;

        using var list = await SendAdminRequestAsync(
            fixture.Client,
            HttpMethod.Get,
            "/admin/v1/access-grants");
        list.EnsureSuccessStatusCode();
        var payload = await list.Content.ReadFromJsonAsync<
            HostApiResponse<IReadOnlyList<HostAccessGrantData>>>(JsonOptions);
        Assert.NotNull(payload);
        var pending = Assert.Single(payload.Data);
        Assert.Equal("pending", pending.State);

        approval.SetResult(new HostAccessGrantPromptResult(
            true,
            AccessGrantScope.Read));
        using var response = await pendingRequest;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task UnlockHostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/unlock")
        {
            Content = JsonContent.Create(new HostUnlockRequest("password"))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateAgentAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/clients")
        {
            Content = JsonContent.Create(
                new HostCreateClientRequest(
                    "grant-agent",
                    "readOnly",
                    "accessGrant"))
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

    private static async Task<HostIssuedAccessGrantData> CreateGrantAsync(
        HttpClient client,
        string agentToken)
    {
        using var response = await SendGrantRequestAsync(
            client,
            agentToken,
            Guid.NewGuid(),
            "read");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostIssuedAccessGrantData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data;
    }

    private static async Task<HttpResponseMessage> SendGrantRequestAsync(
        HttpClient client,
        string agentToken,
        Guid clientInstanceId,
        params string[] scopes)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/mcp/access-grants")
        {
            Content = JsonContent.Create(
                new HostRequestAccessGrantRequest(
                    clientInstanceId,
                    scopes))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            agentToken);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAdminRequestAsync(
        HttpClient client,
        HttpMethod method,
        string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return client.SendAsync(request);
    }
}
