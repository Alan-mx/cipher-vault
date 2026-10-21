using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class AppendEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AppendCreatesNextRevision()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Append note", "first");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(
            note.DocumentId,
            new HostAppendNoteRequest(1, " second"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultWriteResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(note.DocumentId, payload.Data.DocumentId);
        Assert.Equal(2, payload.Data.Revision);
    }

    [Fact]
    public async Task AppendRejectsStaleRevision()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Append note", "first");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest(
            note.DocumentId,
            new HostAppendNoteRequest(99, " stale"));
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
    public async Task AppendRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Append note", "first");
        using var request = CreateRequest(
            note.DocumentId,
            new HostAppendNoteRequest(1, " locked"));
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
        HostAppendNoteRequest body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/v1/notes/{documentId:D}/append")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
