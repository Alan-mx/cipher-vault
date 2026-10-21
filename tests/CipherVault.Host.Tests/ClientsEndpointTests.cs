using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class ClientsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ListClientsReturnsPoliciesWithoutTokenHash()
    {
        var policy = new AgentClientPolicy(
            "codex-readonly",
            new byte[32],
            McpAccessMode.ReadOnly,
            AllowLock: false,
            Enabled: true);
        await using var fixture = await HostTestFixture.CreateAsync(
            clients: [policy]);
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<IReadOnlyList<HostClientData>>>(JsonOptions);
        Assert.NotNull(payload);
        var client = Assert.Single(payload.Data);
        Assert.Equal("codex-readonly", client.Id);
        Assert.Equal("readOnly", client.AccessMode);
        Assert.False(client.AllowLock);
        Assert.True(client.Enabled);
        Assert.DoesNotContain(
            "token",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListClientsRequiresAdminToken()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest();

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateClientReturnsTokenOnceAndPersistsOnlyHash()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateCreateRequest(
            new HostCreateClientRequest("codex-write", "readWrite", true));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("codex-write", payload.Data.Client.Id);
        Assert.Equal("readWrite", payload.Data.Client.AccessMode);
        Assert.True(payload.Data.Client.AllowLock);
        Assert.False(string.IsNullOrWhiteSpace(payload.Data.Token));

        var configText = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("\"configVersion\": 2", configText, StringComparison.Ordinal);
        Assert.Contains("sha256:", configText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(payload.Data.Token, configText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\":", configText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateClientIdIsRejected()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var first = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readOnly", false));
        using var firstResponse = await fixture.Client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        using var second = CreateCreateRequest(
            new HostCreateClientRequest("CODEX", "readWrite", true));
        using var secondResponse = await fixture.Client.SendAsync(second);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        var payload = await secondResponse.Content.ReadFromJsonAsync<
            HostApiErrorResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("already_exists", payload.Error.Code);
    }

    [Fact]
    public async Task InvalidAccessModeIsRejected()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateCreateRequest(
            new HostCreateClientRequest("codex", "admin", false));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
        Assert.False(File.Exists(fixture.ConfigPath));
    }

    [Fact]
    public async Task UpdateClientChangesPolicyAndPersists()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var create = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readOnly", false));
        using var createResponse = await fixture.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var update = CreateUpdateRequest(
            "codex",
            new HostUpdateClientRequest("readWrite", true, false));
        using var updateResponse = await fixture.Client.SendAsync(update);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var payload = await updateResponse.Content.ReadFromJsonAsync<
            HostApiResponse<HostClientData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("readWrite", payload.Data.AccessMode);
        Assert.True(payload.Data.AllowLock);
        Assert.False(payload.Data.Enabled);
        var configText = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("\"enabled\": false", configText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateMissingClientReturnsNotFound()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateUpdateRequest(
            "missing",
            new HostUpdateClientRequest("readOnly", false, true));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("not_found", payload.Error.Code);
    }

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/clients");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }

    private static HttpRequestMessage CreateCreateRequest(
        HostCreateClientRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/clients")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return request;
    }

    private static HttpRequestMessage CreateUpdateRequest(
        string clientId,
        HostUpdateClientRequest body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/admin/v1/clients/{clientId}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return request;
    }
}
