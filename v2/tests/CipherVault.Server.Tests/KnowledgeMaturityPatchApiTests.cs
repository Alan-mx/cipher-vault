using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeMaturityPatchApiTests
{
    [Fact]
    public async Task UserUiCanSetAndClearMaturityButMcpCannotUseSet()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var markdown = await CreateAsync(
            server.Client,
            csrf,
            VaultDocumentFormat.Markdown,
            "status: active");

        using var set = await server.Client.SendWithRevisionAsync(
            HttpMethod.Patch,
            $"/api/documents/{markdown.DocumentId:D}",
            csrf,
            markdown.Revision,
            new
            {
                targetKind = "front_matter",
                frontMatterKey = "maturity",
                operation = "set",
                value = "raw",
                dryRun = false
            });
        var setBody = await set.Content.ReadAsStringAsync();
        Assert.True(set.IsSuccessStatusCode, setBody);
        using var setJson = JsonDocument.Parse(setBody);
        Assert.True(setJson.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(2, setJson.RootElement.GetProperty("revision").GetInt32());

        using var queue = await server.Client.GetAsync(
            "/api/knowledge/views/maturity/queue"
            + "?bucket=pending_organization");
        using var queueJson = await ReadJsonAsync(queue);
        var item = queueJson.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Single(candidate =>
                candidate.GetProperty("documentId").GetGuid() ==
                markdown.DocumentId);
        Assert.Equal("raw", item.GetProperty("maturityValue").GetString());
        Assert.Equal("excerpt", item.GetProperty("nextMaturity").GetString());

        using var cleared = await server.Client.SendWithRevisionAsync(
            HttpMethod.Patch,
            $"/api/documents/{markdown.DocumentId:D}",
            csrf,
            2,
            new
            {
                targetKind = "front_matter",
                frontMatterKey = "maturity",
                operation = "delete",
                dryRun = false
            });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var token = await McpTestClient.CreateTokenAsync(server.Client);
        var mcpResult = await McpTestClient.CallToolAsync(
            server.Client,
            token,
            "patch_note",
            new
            {
                documentId = markdown.DocumentId.ToString("D"),
                expectedRevision = 3,
                idempotencyKey = Guid.NewGuid().ToString("D"),
                targetKind = "front_matter",
                frontmatterKey = "maturity",
                operation = "set",
                value = "raw"
            });
        Assert.Equal("invalid_input", mcpResult.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SetRejectsTextAndOtherFrontMatterKeys()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var text = await CreateAsync(
            server.Client,
            csrf,
            VaultDocumentFormat.Text,
            null);
        var markdown = await CreateAsync(
            server.Client,
            csrf,
            VaultDocumentFormat.Markdown,
            "status: active");

        using var textResponse = await server.Client.SendWithRevisionAsync(
            HttpMethod.Patch,
            $"/api/documents/{text.DocumentId:D}",
            csrf,
            text.Revision,
            SetBody("raw"));
        Assert.Equal(HttpStatusCode.BadRequest, textResponse.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(textResponse));

        using var keyResponse = await server.Client.SendWithRevisionAsync(
            HttpMethod.Patch,
            $"/api/documents/{markdown.DocumentId:D}",
            csrf,
            markdown.Revision,
            SetBody("raw", "status"));
        Assert.Equal(HttpStatusCode.BadRequest, keyResponse.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidInput,
            await ReadErrorCodeAsync(keyResponse));
    }

    private static object SetBody(
        string value,
        string key = "maturity") =>
        new
        {
            targetKind = "front_matter",
            frontMatterKey = key,
            operation = "set",
            value,
            dryRun = false
        };

    private static async Task<(Guid DocumentId, int Revision)> CreateAsync(
        HttpClient client,
        string token,
        VaultDocumentFormat format,
        string? frontMatter)
    {
        var content = format == VaultDocumentFormat.Text
            ? "Plain text."
            : $"""
               ---
               {frontMatter}
               ---

               # Markdown
               """;
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Maturity",
                null,
                null,
                format,
                content,
                null));
        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response);
        return (
            json.RootElement.GetProperty("documentId").GetGuid(),
            json.RootElement.GetProperty("revision").GetInt32());
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response)
    {
        using var json = await ReadJsonAsync(response);
        return json.RootElement.GetProperty("code").GetString();
    }
}
