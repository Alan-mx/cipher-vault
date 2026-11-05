using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeMaturityQueueApiTests
{
    [Fact]
    public async Task QueueReturnsBucketsMetadataAndNoStore()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        _ = await CreateAsync(
            server.Client,
            token,
            "Raw",
            VaultDocumentFormat.Markdown,
            "maturity: RAW");
        _ = await CreateAsync(
            server.Client,
            token,
            "Organized",
            VaultDocumentFormat.Markdown,
            "maturity: \" Organized \"\ntags: [Work]");
        _ = await CreateAsync(
            server.Client,
            token,
            "Verified",
            VaultDocumentFormat.Markdown,
            "maturity: verified");
        _ = await CreateAsync(
            server.Client,
            token,
            "Other",
            VaultDocumentFormat.Markdown,
            "maturity: reviewed");
        _ = await CreateAsync(
            server.Client,
            token,
            "Number",
            VaultDocumentFormat.Markdown,
            "maturity: 7");
        _ = await CreateAsync(
            server.Client,
            token,
            "Text",
            VaultDocumentFormat.Text,
            null);

        using var pending = await server.Client.GetAsync(
            "/api/knowledge/views/maturity/queue"
            + "?bucket=pending_organization");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal("no-store", pending.Headers.CacheControl?.ToString());
        using var pendingJson = await ReadJsonAsync(pending);
        Assert.Equal(
            3,
            pendingJson.RootElement.GetProperty("items").GetArrayLength());
        var number = pendingJson.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == "Number");
        Assert.Equal(
            "number",
            number.GetProperty("maturityValueType").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            number.GetProperty("maturityValue").ValueKind);
        Assert.Equal("raw", number.GetProperty("nextMaturity").GetString());
        Assert.Equal(
            "pending_organization",
            number.GetProperty("maturityBucket").GetString());

        using var text = await server.Client.GetAsync(
            "/api/knowledge/views/maturity/queue"
            + "?bucket=pending_organization");
        using var textJson = await ReadJsonAsync(text);
        var textItem = textJson.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == "Text");
        Assert.Equal("txt", textItem.GetProperty("format").GetString());
        Assert.Equal(
            "missing",
            textItem.GetProperty("maturityValueType").GetString());

        using var verification = await server.Client.GetAsync(
            "/api/knowledge/views/maturity/queue"
            + "?bucket=pending_verification&pageSize=1");
        using var verificationJson = await ReadJsonAsync(verification);
        var organized = Assert.Single(
            verificationJson.RootElement
                .GetProperty("items")
                .EnumerateArray());
        Assert.Equal(" Organized ", organized.GetProperty("maturityValue")
            .GetString());
        Assert.Equal(
            "verified",
            organized.GetProperty("nextMaturity").GetString());
        Assert.Equal(
            ["Work"],
            organized.GetProperty("tags")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.False(organized.TryGetProperty("frontMatter", out _));
        Assert.False(organized.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task QueueRejectsInvalidQueriesAndCursorScopeChanges()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        _ = await CreateAsync(
            server.Client,
            token,
            "Organized One",
            VaultDocumentFormat.Markdown,
            "maturity: organized");
        _ = await CreateAsync(
            server.Client,
            token,
            "Organized Two",
            VaultDocumentFormat.Markdown,
            "maturity: organized");

        using var first = await server.Client.GetAsync(
            "/api/knowledge/views/maturity/queue"
            + "?bucket=pending_verification&pageSize=1");
        using var firstJson = await ReadJsonAsync(first);
        var cursor = firstJson.RootElement.GetProperty("nextCursor")
            .GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        using var escaped = await server.Client.GetAsync(
            "/api/knowledge/views/maturity/queue"
            + "?bucket=pending_verification&pageSize=2"
            + $"&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, escaped.StatusCode);
        Assert.Equal(
            ApiErrorCodes.InvalidCursor,
            await ReadErrorCodeAsync(escaped));

        var invalidPaths = new[]
        {
            "/api/knowledge/views/maturity/queue",
            "/api/knowledge/views/maturity/queue?bucket=unknown",
            "/api/knowledge/views/maturity/queue?bucket=completed&bucket=other",
            "/api/knowledge/views/maturity/queue?bucket=completed&pageSize=abc",
            "/api/knowledge/views/maturity/queue?bucket=completed&pageSize=0",
            "/api/knowledge/views/maturity/queue?bucket=completed&pageSize=101",
            "/api/knowledge/views/maturity/queue?bucket=completed&query=redis"
        };
        foreach (var path in invalidPaths)
        {
            using var response = await server.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(
                ApiErrorCodes.InvalidInput,
                await ReadErrorCodeAsync(response));
        }

        using var unauthorizedClient = server.CreateClient();
        using var unauthorized = await unauthorizedClient.GetAsync(
            "/api/knowledge/views/maturity/queue"
            + "?bucket=completed");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(
            "no-store",
            unauthorized.Headers.CacheControl?.ToString());
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        string token,
        string title,
        VaultDocumentFormat format,
        string? frontMatter)
    {
        var content = frontMatter is null
            ? "Plain text."
            : $"""
               ---
               {frontMatter}
               ---

               # {title}
               """;
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                title,
                null,
                null,
                format,
                content,
                null));
        response.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(response);
        return json.RootElement.GetProperty("documentId").GetGuid();
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
