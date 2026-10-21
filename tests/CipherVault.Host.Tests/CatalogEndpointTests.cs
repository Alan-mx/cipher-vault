using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class CatalogEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CatalogReturnsSeededDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await fixture.CreateNoteAsync(
            "catalog note",
            "catalog body",
            folder: "inbox");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateCatalogRequest("inbox");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultCatalogPage>>(JsonOptions);
        Assert.NotNull(payload);
        var item = Assert.Single(payload.Data.Items);
        Assert.Equal("catalog note", item.Title);
        Assert.Equal("inbox/catalog note.md", item.VirtualPath);
    }

    [Fact]
    public async Task CatalogRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateCatalogRequest(null);
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

    private static HttpRequestMessage CreateCatalogRequest(string? folder)
    {
        var path = folder is null
            ? "/admin/v1/catalog"
            : $"/admin/v1/catalog?folder={Uri.EscapeDataString(folder)}";
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
