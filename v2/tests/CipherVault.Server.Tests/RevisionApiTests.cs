using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class RevisionApiTests
{
    [Fact]
    public async Task RevisionListAndRestoreCreateNewCurrentRevision()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var documentId = await CreateAsync(server.Client, token);
        var path = $"/api/documents/{documentId:D}";

        using var updated = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            path,
            token,
            revision: 1,
            new UpdateDocumentRequest(
                "Revision API",
                null,
                "version two",
                []));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var list = await server.Client.GetAsync($"{path}/revisions");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = JsonDocument.Parse(
            await list.Content.ReadAsStringAsync());
        var revisions = listJson.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, revisions.Length);
        var versionOneId = revisions
            .Single(item => item.GetProperty("revision").GetInt32() == 1)
            .GetProperty("revisionId")
            .GetGuid();

        using var restored = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Post,
            $"{path}/revisions/{versionOneId:D}/restore",
            token,
            revision: 2);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("\"3\"", restored.Headers.ETag?.ToString());

        using var content = await server.Client.GetAsync(
            $"{path}/content?offset=0&maxChars=100");
        using var contentJson = JsonDocument.Parse(
            await content.Content.ReadAsStringAsync());
        Assert.Equal(
            "version one",
            contentJson.RootElement.GetProperty("content").GetString());
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        string token)
    {
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Revision API",
                null,
                null,
                VaultDocumentFormat.Text,
                "version one",
                null));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("documentId").GetGuid();
    }
}
