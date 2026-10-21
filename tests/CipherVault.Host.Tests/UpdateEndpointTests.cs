using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class UpdateEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UpdateChangesDocumentAndCreatesRevision()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync(
            "Old title",
            "old content",
            folder: "inbox");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(
            note.DocumentId,
            new HostUpdateNoteRequest(
                1,
                "New title",
                "archive",
                "updated content",
                ["updated"]));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultWriteResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(2, payload.Data.Revision);

        using var readRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/v1/notes/{note.DocumentId:D}");
        readRequest.Headers.Host = "127.0.0.1:43127";
        readRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var readResponse = await fixture.Client.SendAsync(readRequest);
        readResponse.EnsureSuccessStatusCode();
        var read = await readResponse.Content.ReadFromJsonAsync<
            HostApiResponse<VaultReadResult>>(JsonOptions);
        Assert.NotNull(read);
        Assert.Equal("New title", read.Data.Title);
        Assert.Equal("archive/New title.md", read.Data.VirtualPath);
        Assert.Equal("updated content", read.Data.Content);
    }

    [Fact]
    public async Task UpdateRejectsStaleRevision()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Update note", "content");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(
            note.DocumentId,
            new HostUpdateNoteRequest(99, "Stale", null, null, null));
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
    public async Task UpdateRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Update note", "content");
        using var request = CreateRequest(
            note.DocumentId,
            new HostUpdateNoteRequest(1, "Locked", null, null, null));
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
        HostUpdateNoteRequest body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/admin/v1/notes/{documentId:D}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
