using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class TrashEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TrashRemovesDocumentFromCatalog()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Trash note", "content");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(
            note.DocumentId,
            new HostTrashNoteRequest(1));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultDeleteResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(note.DocumentId, payload.Data.DocumentId);
        Assert.Equal(1, payload.Data.Revision);
        Assert.True(payload.Data.PurgeAfter > DateTimeOffset.UtcNow);

        using var catalogRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/catalog");
        catalogRequest.Headers.Host = "127.0.0.1:43127";
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var catalogResponse = await fixture.Client.SendAsync(catalogRequest);
        catalogResponse.EnsureSuccessStatusCode();
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<
            HostApiResponse<VaultCatalogPage>>(JsonOptions);
        Assert.NotNull(catalog);
        Assert.Empty(catalog.Data.Items);
    }

    [Fact]
    public async Task TrashRejectsStaleRevision()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Trash note", "content");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(
            note.DocumentId,
            new HostTrashNoteRequest(99));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("conflict", payload.Error.Code);
    }

    [Fact]
    public async Task TrashRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Trash note", "content");
        using var request = CreateRequest(
            note.DocumentId,
            new HostTrashNoteRequest(1));
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

    private static HttpRequestMessage CreateRequest(
        Guid documentId,
        HostTrashNoteRequest body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/v1/notes/{documentId:D}/trash")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
