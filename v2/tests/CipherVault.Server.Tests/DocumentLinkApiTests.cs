using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class DocumentLinkApiTests
{
    [Fact]
    public async Task BacklinksAndUnlinkedMentionsUseApplicationPages()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        var targetId = await CreateAsync(
            server.Client,
            token,
            "Target Document",
            "Target body");
        _ = await CreateAsync(
            server.Client,
            token,
            "Linked Source",
            "See [[Target Document]].");
        _ = await CreateAsync(
            server.Client,
            token,
            "Mention Source",
            "Target Document appears here.");

        using var backlinks = await server.Client.GetAsync(
            $"/api/documents/{targetId:D}/backlinks");
        Assert.Equal(HttpStatusCode.OK, backlinks.StatusCode);
        using var backlinkJson = JsonDocument.Parse(
            await backlinks.Content.ReadAsStringAsync());
        Assert.Single(backlinkJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            "Linked Source",
            backlinkJson.RootElement
                .GetProperty("items")[0]
                .GetProperty("sourceTitle")
                .GetString());

        using var mentions = await server.Client.GetAsync(
            $"/api/documents/{targetId:D}/unlinked-mentions");
        Assert.Equal(HttpStatusCode.OK, mentions.StatusCode);
        using var mentionJson = JsonDocument.Parse(
            await mentions.Content.ReadAsStringAsync());
        Assert.Contains(
            mentionJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("sourceTitle").GetString()
                == "Mention Source");
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        string token,
        string title,
        string content)
    {
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                title,
                null,
                null,
                VaultDocumentFormat.Markdown,
                content,
                null));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("documentId").GetGuid();
    }
}
