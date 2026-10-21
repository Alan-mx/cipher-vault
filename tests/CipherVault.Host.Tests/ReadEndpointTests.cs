using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class ReadEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReadReturnsRequestedContentRange()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync(
            "Read note",
            "0123456789",
            folder: "inbox");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateReadRequest(note.DocumentId, offset: 2, maxChars: 4);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultReadResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("2345", payload.Data.Content);
        Assert.Equal(2, payload.Data.Offset);
        Assert.Equal(6, payload.Data.NextOffset);
        Assert.Equal(10, payload.Data.TotalLength);
        Assert.False(payload.Data.Done);
    }

    [Fact]
    public async Task ReadRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Read note", "content");
        using var request = CreateReadRequest(note.DocumentId);
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
    public async Task ReadRejectsInvalidRange()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var note = await fixture.CreateNoteAsync("Read note", "content");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateReadRequest(
            note.DocumentId,
            offset: -1,
            maxChars: 4);
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

    private static HttpRequestMessage CreateReadRequest(
        Guid documentId,
        int? offset = null,
        int? maxChars = null)
    {
        var query = new List<string>();
        if (offset is not null)
        {
            query.Add($"offset={offset.Value}");
        }
        if (maxChars is not null)
        {
            query.Add($"maxChars={maxChars.Value}");
        }
        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join("&", query);
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/v1/notes/{documentId:D}{suffix}");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
