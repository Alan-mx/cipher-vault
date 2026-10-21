using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class RestoreTrashEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RestoreReturnsDocumentToCatalog()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync(
            "Restore note",
            "content",
            folder: "inbox");
        await UnlockHostAsync(fixture.Client);
        await TrashAsync(fixture.Client, note.DocumentId);
        using var request = CreateRequest(
            note.DocumentId,
            new HostRestoreTrashRequest(1, null));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultRestoreResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(note.DocumentId, payload.Data.DocumentId);
        Assert.Equal(2, payload.Data.Revision);
        Assert.Equal("inbox/Restore note.md", payload.Data.VirtualPath);

        using var catalogRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/catalog?folder=inbox");
        catalogRequest.Headers.Host = "127.0.0.1:43127";
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var catalogResponse = await fixture.Client.SendAsync(catalogRequest);
        catalogResponse.EnsureSuccessStatusCode();
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<
            HostApiResponse<VaultCatalogPage>>(JsonOptions);
        Assert.NotNull(catalog);
        Assert.Single(catalog.Data.Items);
    }

    [Fact]
    public async Task RestoreRejectsStaleRevision()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Restore note", "content");
        await UnlockHostAsync(fixture.Client);
        await TrashAsync(fixture.Client, note.DocumentId);
        using var request = CreateRequest(
            note.DocumentId,
            new HostRestoreTrashRequest(99, null));
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
    public async Task RestoreRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest(
            Guid.NewGuid(),
            new HostRestoreTrashRequest(1, "renamed.md"));
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

    private static async Task TrashAsync(HttpClient client, Guid documentId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/v1/notes/{documentId:D}/trash")
        {
            Content = JsonContent.Create(new HostTrashNoteRequest(1))
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
        HostRestoreTrashRequest body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/v1/trash/{documentId:D}/restore")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
