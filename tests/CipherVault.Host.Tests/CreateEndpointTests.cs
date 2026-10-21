using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class CreateEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateStoresDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(new HostCreateNoteRequest(
            "Created through Host",
            "host",
            "md",
            "# Created\nHost content.",
            ["integration"]));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultWriteResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(1, payload.Data.Revision);

        using var catalogRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/catalog?folder=host");
        catalogRequest.Headers.Host = "127.0.0.1:43127";
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var catalogResponse = await fixture.Client.SendAsync(catalogRequest);
        catalogResponse.EnsureSuccessStatusCode();
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<
            HostApiResponse<VaultCatalogPage>>(JsonOptions);
        Assert.NotNull(catalog);
        var item = Assert.Single(catalog.Data.Items);
        Assert.Equal("Created through Host", item.Title);
        Assert.Equal(["integration"], item.Tags);
    }

    [Fact]
    public async Task CreateRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest(new HostCreateNoteRequest(
            "Locked note",
            null,
            "md",
            "content",
            null));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("session_locked", payload.Error.Code);
    }

    [Fact]
    public async Task CreateRejectsInvalidDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(new HostCreateNoteRequest(
            " ",
            null,
            "exe",
            "content",
            null));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_input", payload.Error.Code);
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

    private static HttpRequestMessage CreateRequest(HostCreateNoteRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/notes")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
