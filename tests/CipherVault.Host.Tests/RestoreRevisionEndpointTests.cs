using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class RestoreRevisionEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RestoreRevisionRollsBackContent()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Revision note", "first");
        await UnlockHostAsync(fixture.Client);
        await AppendAsync(fixture.Client, note.DocumentId, " second");
        var revisions = await ListRevisionsAsync(fixture.Client, note.DocumentId);
        var firstRevision = Assert.Single(
            revisions.Items,
            item => item.Revision == 1);
        using var request = CreateRequest(
            note.DocumentId,
            firstRevision.RevisionId,
            new HostRestoreRevisionRequest(2));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultWriteResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(3, payload.Data.Revision);

        var restored = await ReadAsync(fixture.Client, note.DocumentId);
        Assert.Equal("first", restored.Content);
        Assert.Equal(3, restored.Revision);
    }

    [Fact]
    public async Task RestoreRevisionRejectsStaleRevision()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Revision note", "first");
        await UnlockHostAsync(fixture.Client);
        await AppendAsync(fixture.Client, note.DocumentId, " second");
        var revisions = await ListRevisionsAsync(fixture.Client, note.DocumentId);
        var firstRevision = Assert.Single(
            revisions.Items,
            item => item.Revision == 1);
        using var request = CreateRequest(
            note.DocumentId,
            firstRevision.RevisionId,
            new HostRestoreRevisionRequest(99));
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
    public async Task RestoreRevisionRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new HostRestoreRevisionRequest(1));
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

    private static async Task AppendAsync(
        HttpClient client,
        Guid documentId,
        string content)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/v1/notes/{documentId:D}/append")
        {
            Content = JsonContent.Create(new HostAppendNoteRequest(1, content))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<VaultRevisionPage> ListRevisionsAsync(
        HttpClient client,
        Guid documentId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/v1/notes/{documentId:D}/revisions");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultRevisionPage>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data;
    }

    private static async Task<VaultReadResult> ReadAsync(
        HttpClient client,
        Guid documentId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/v1/notes/{documentId:D}");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultReadResult>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data;
    }

    private static HttpRequestMessage CreateRequest(
        Guid documentId,
        Guid revisionId,
        HostRestoreRevisionRequest body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/v1/notes/{documentId:D}/revisions/{revisionId:D}/restore")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
