using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class TrashListEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TrashListReturnsDeletedDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync(
            "Deleted note",
            "content",
            folder: "inbox");
        await UnlockHostAsync(fixture.Client);
        await TrashAsync(fixture.Client, note.DocumentId);
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultTrashPage>>(JsonOptions);
        Assert.NotNull(payload);
        var item = Assert.Single(payload.Data.Items);
        Assert.Equal(note.DocumentId, item.DocumentId);
        Assert.Equal("Deleted note", item.Title);
        Assert.Equal(1, item.Revision);
        Assert.True(item.PurgeAfter > item.DeletedAt);
    }

    [Fact]
    public async Task TrashListRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest();
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

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/trash");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
