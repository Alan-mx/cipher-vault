using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class DocumentCopyApiTests
{
    [Fact]
    public async Task CopyCreatesIndependentDocumentAndReplaysIdempotently()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        using var createdResponse =
            await server.Client.PostJsonWithAntiforgeryAsync(
                "/api/documents",
                token,
                new CreateDocumentRequest(
                    "Source",
                    "notes/source.md",
                    null,
                    VaultDocumentFormat.Markdown,
                    "# Source",
                    null));
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        using var createdJson = JsonDocument.Parse(
            await createdResponse.Content.ReadAsStringAsync());
        var sourceId = createdJson.RootElement
            .GetProperty("documentId")
            .GetGuid();
        var key = Guid.NewGuid();

        using var first = await CopyAsync(server, token, sourceId, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstJson = JsonDocument.Parse(
            await first.Content.ReadAsStringAsync());
        var copyId = firstJson.RootElement.GetProperty("documentId").GetGuid();
        Assert.NotEqual(sourceId, copyId);
        Assert.Equal("\"1\"", first.Headers.ETag?.ToString());

        using var replay = await CopyAsync(server, token, sourceId, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(
            await replay.Content.ReadAsStringAsync());
        Assert.Equal(
            copyId,
            replayJson.RootElement.GetProperty("documentId").GetGuid());

        using var content = await server.Client.GetAsync(
            $"/api/documents/{copyId:D}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        using var contentJson = JsonDocument.Parse(
            await content.Content.ReadAsStringAsync());
        Assert.Contains(
            $"id: {copyId:D}",
            contentJson.RootElement.GetProperty("content").GetString(),
            StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> CopyAsync(
        ServerTestApplication server,
        string token,
        Guid sourceId,
        Guid key)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/documents/{sourceId:D}/copy")
        {
            Content = JsonContent.Create(new { title = "Source copy" })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return server.Client.SendAsync(request);
    }
}
