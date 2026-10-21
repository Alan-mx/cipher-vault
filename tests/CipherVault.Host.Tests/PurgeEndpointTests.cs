using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class PurgeEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PurgePermanentlyRemovesTrashedDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Purge note", "content");
        await UnlockHostAsync(fixture.Client);
        await TrashAsync(fixture.Client, note.DocumentId);
        using var request = CreateRequest(note.DocumentId);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultPurgeResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(note.DocumentId, payload.Data.DocumentId);
        Assert.True(payload.Data.Purged);

        using var listRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/trash");
        listRequest.Headers.Host = "127.0.0.1:43127";
        listRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var listResponse = await fixture.Client.SendAsync(listRequest);
        listResponse.EnsureSuccessStatusCode();
        var trash = await listResponse.Content.ReadFromJsonAsync<
            HostApiResponse<VaultTrashPage>>(JsonOptions);
        Assert.NotNull(trash);
        Assert.Empty(trash.Data.Items);
    }

    [Fact]
    public async Task PurgeRejectsActiveDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Active note", "content");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(note.DocumentId);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("not_found", payload.Error.Code);
    }

    [Fact]
    public async Task PurgeRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest(Guid.NewGuid());
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

    private static HttpRequestMessage CreateRequest(Guid documentId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/admin/v1/trash/{documentId:D}");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
