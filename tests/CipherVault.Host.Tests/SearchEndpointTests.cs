using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class SearchEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SearchReturnsMatchingDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await fixture.CreateNoteAsync(
            "Search note",
            "The encrypted search marker is lighthouse.",
            folder: "inbox");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateSearchRequest("lighthouse");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultSearchPage>>(JsonOptions);
        Assert.NotNull(payload);
        var hit = Assert.Single(payload.Data.Hits);
        Assert.Equal("Search note", hit.Title);
        Assert.Contains("[lig", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, payload.Data.Coverage.ReturnedDocuments);
    }

    [Fact]
    public async Task SearchRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateSearchRequest("marker");
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
    public async Task SearchRejectsBlankQuery()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        using var request = CreateSearchRequest(" ");
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

    private static HttpRequestMessage CreateSearchRequest(string query)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/v1/search?q={Uri.EscapeDataString(query)}");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
