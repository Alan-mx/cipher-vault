using System.Net;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class DocumentUpdateTagsApiTests
{
    [Fact]
    public async Task OmittedMarkdownTagsAreDerivedFromFrontMatter()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var documentId = await CreateAsync(
            server,
            token,
            VaultDocumentFormat.Markdown,
            "---\ntags: [Old]\n---\nbody",
            null);

        using var updated = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            $"/api/documents/{documentId:D}",
            token,
            1,
            new
            {
                title = "Tags",
                content = "---\ntags: [New]\n---\nupdated"
            });
        var updatedBody = await updated.Content.ReadAsStringAsync();
        Assert.True(updated.IsSuccessStatusCode, updatedBody);
        Assert.Equal("\"2\"", updated.Headers.ETag?.ToString());

        var metadata = await GetMetadataAsync(server, documentId);
        Assert.Equal(["New"], metadata.Tags);
    }

    [Fact]
    public async Task OmittedTextTagsArePreservedAndEmptyTagsClear()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var documentId = await CreateAsync(
            server,
            token,
            VaultDocumentFormat.Text,
            "body",
            ["Keep"]);

        using var preserved = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            $"/api/documents/{documentId:D}",
            token,
            1,
            new
            {
                title = "Tags",
                content = "body 2"
            });
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.Equal(
            ["Keep"],
            (await GetMetadataAsync(server, documentId)).Tags);

        using var cleared = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            $"/api/documents/{documentId:D}",
            token,
            2,
            new
            {
                title = "Tags",
                content = "body 3",
                tags = Array.Empty<string>()
            });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await GetMetadataAsync(server, documentId)).Tags);
    }

    [Fact]
    public async Task ExplicitNullAndMismatchedTagsAreRejectedWithoutWriting()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var documentId = await CreateAsync(
            server,
            token,
            VaultDocumentFormat.Markdown,
            "---\ntags: [Old]\n---\nbody",
            null);

        using var explicitNullRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/documents/{documentId:D}")
        {
            Content = new StringContent(
                """
                {
                  "title": "Tags",
                  "content": "---\ntags: [New]\n---\nbody",
                  "tags": null
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
        explicitNullRequest.Headers.Add("X-CSRF-TOKEN", token);
        explicitNullRequest.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var explicitNull = await server.Client.SendAsync(explicitNullRequest);
        Assert.Equal(HttpStatusCode.BadRequest, explicitNull.StatusCode);

        using var mismatch = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            $"/api/documents/{documentId:D}",
            token,
            1,
            new
            {
                title = "Tags",
                content = "---\ntags: [New]\n---\nbody",
                tags = new[] { "Other" }
            });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal(1, (await GetMetadataAsync(server, documentId)).Revision);
    }

    [Fact]
    public async Task RemovedUpdateBacklinksFieldIsRejected()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var documentId = await CreateAsync(
            server,
            token,
            VaultDocumentFormat.Markdown,
            "body",
            null);

        using var response = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            $"/api/documents/{documentId:D}",
            token,
            1,
            new
            {
                title = "Tags",
                content = "updated",
                updateBacklinks = false
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, (await GetMetadataAsync(server, documentId)).Revision);
    }

    private static async Task<Guid> CreateAsync(
        ServerTestApplication server,
        string token,
        VaultDocumentFormat format,
        string content,
        IReadOnlyList<string>? tags)
    {
        using var response = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Tags",
                null,
                null,
                format,
                content,
                tags));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("documentId").GetGuid();
    }

    private static async Task<MetadataSnapshot> GetMetadataAsync(
        ServerTestApplication server,
        Guid documentId)
    {
        using var response = await server.Client.GetAsync(
            $"/api/documents/{documentId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return new MetadataSnapshot(
            json.RootElement.GetProperty("revision").GetInt32(),
            json.RootElement.GetProperty("tags")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    private sealed record MetadataSnapshot(
        int Revision,
        IReadOnlyList<string> Tags);
}
