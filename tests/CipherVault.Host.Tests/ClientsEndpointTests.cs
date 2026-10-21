using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.AccessControl;
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
            AgentAuthorizationMode.AccessGrant,
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
        Assert.Equal("accessGrant", client.AuthorizationMode);
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
            new HostCreateClientRequest("codex-write", "readWrite", "accessGrant"));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("codex-write", payload.Data.Client.Id);
        Assert.Equal("readWrite", payload.Data.Client.AccessMode);
        Assert.Equal("accessGrant", payload.Data.Client.AuthorizationMode);
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
            new HostCreateClientRequest("codex", "readOnly", "accessGrant"));
        using var firstResponse = await fixture.Client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        using var second = CreateCreateRequest(
            new HostCreateClientRequest("CODEX", "readWrite", "accessGrant"));
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
            new HostCreateClientRequest("codex", "admin", "accessGrant"));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
        Assert.False(File.Exists(fixture.ConfigPath));
    }

    [Fact]
    public async Task StaticTokenWithWriteAccessIsRejected()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateCreateRequest(
            new HostCreateClientRequest(
                "codex",
                "readWrite",
                "staticToken"));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
        Assert.False(File.Exists(fixture.ConfigPath));
    }

    [Fact]
    public async Task CreateClientDefaultsToAccessGrant()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readWrite", null));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("accessGrant", payload.Data.Client.AuthorizationMode);
    }

    [Fact]
    public async Task UpdateClientChangesPolicyAndPersists()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var create = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readOnly", "accessGrant"));
        using var createResponse = await fixture.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var update = CreateUpdateRequest(
            "codex",
            new HostUpdateClientRequest("readWrite", "accessGrant", false));
        using var updateResponse = await fixture.Client.SendAsync(update);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var payload = await updateResponse.Content.ReadFromJsonAsync<
            HostApiResponse<HostClientData>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("readWrite", payload.Data.AccessMode);
        Assert.Equal("accessGrant", payload.Data.AuthorizationMode);
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
            new HostUpdateClientRequest("readOnly", "accessGrant", true));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("not_found", payload.Error.Code);
    }

    [Fact]
    public async Task UpdateToStaticTokenWithWriteAccessIsRejected()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var create = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readWrite", "accessGrant"));
        using var createResponse = await fixture.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var update = CreateUpdateRequest(
            "codex",
            new HostUpdateClientRequest("readWrite", "staticToken", true));
        using var updateResponse = await fixture.Client.SendAsync(update);

        Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
        var payload = await updateResponse.Content.ReadFromJsonAsync<
            HostApiErrorResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
    }

    [Fact]
    public async Task RotateClientReturnsNewTokenAndReplacesHash()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var create = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readOnly", "accessGrant"));
        using var createResponse = await fixture.Client.SendAsync(create);
        var created = await createResponse.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(created);
        var configBefore = await File.ReadAllTextAsync(fixture.ConfigPath);

        using var rotate = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/clients/codex/rotate");
        rotate.Headers.Host = "127.0.0.1:43127";
        rotate.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var rotateResponse = await fixture.Client.SendAsync(rotate);

        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        var rotated = await rotateResponse.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(rotated);
        Assert.NotEqual(created.Data.Token, rotated.Data.Token);
        Assert.Equal("codex", rotated.Data.Client.Id);
        var configAfter = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.NotEqual(configBefore, configAfter);
        Assert.DoesNotContain(rotated.Data.Token, configAfter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotateMissingClientReturnsNotFound()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/clients/missing/rotate");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteClientRemovesPolicyAndPersists()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var create = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readOnly", "accessGrant"));
        using var createResponse = await fixture.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var delete = CreateDeleteRequest("codex");
        using var deleteResponse = await fixture.Client.SendAsync(delete);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        var configText = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("\"clients\": []", configText, StringComparison.Ordinal);

        using var list = CreateRequest();
        list.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var listResponse = await fixture.Client.SendAsync(list);
        var payload = await listResponse.Content.ReadFromJsonAsync<
            HostApiResponse<IReadOnlyList<HostClientData>>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Empty(payload.Data);
    }

    [Fact]
    public async Task DeleteMissingClientReturnsNotFound()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateDeleteRequest("missing");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreatingClientBeyondLimitIsRejectedWithoutWritingConfig()
    {
        var policies = Enumerable.Range(
                0,
                CipherVaultHostConfiguration.MaximumClients)
            .Select(index => new AgentClientPolicy(
                $"client-{index}",
                new byte[32],
                McpAccessMode.ReadOnly,
                AgentAuthorizationMode.AccessGrant,
                Enabled: true))
            .ToList();
        await using var fixture = await HostTestFixture.CreateAsync(
            clients: policies);
        using var request = CreateCreateRequest(
            new HostCreateClientRequest("overflow", "readOnly", "accessGrant"));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(fixture.ConfigPath));
    }

    [Fact]
    public async Task PersistingClientDoesNotProtectCustomParentDirectory()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var protectedBefore = IsDirectoryAclProtected(fixture.Root);
        using var request = CreateCreateRequest(
            new HostCreateClientRequest("codex", "readOnly", "accessGrant"));

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            protectedBefore,
            IsDirectoryAclProtected(fixture.Root));
        Assert.True(
            new FileInfo(fixture.ConfigPath)
                .GetAccessControl()
                .AreAccessRulesProtected);
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

    private static HttpRequestMessage CreateDeleteRequest(string clientId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/admin/v1/clients/{clientId}");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        return request;
    }

    private static bool IsDirectoryAclProtected(string path)
    {
        return new DirectoryInfo(path)
            .GetAccessControl()
            .AreAccessRulesProtected;
    }
}
