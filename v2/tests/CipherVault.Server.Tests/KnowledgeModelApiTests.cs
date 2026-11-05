using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeModelApiTests
{
    [Fact]
    public async Task GetAndReplaceKnowledgeModel()
    {
        var logs = new CapturingLoggerProvider();
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true,
            configureLogging: builder => builder.AddProvider(logs));
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var get = await server.Client.GetAsync("/api/knowledge-model");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(
            "\"1\"",
            get.Headers.ETag?.Tag);
        using var initial = JsonDocument.Parse(
            await get.Content.ReadAsStringAsync());
        var types = initial.RootElement.GetProperty("knowledgeTypes")
            .EnumerateArray()
            .ToArray();
        var relationTypes = initial.RootElement.GetProperty("relationTypes")
            .EnumerateArray()
            .ToArray();
        var sourceRules = initial.RootElement.GetProperty("sourceRules")
            .EnumerateArray()
            .ToArray();

        var body = new
        {
            knowledgeTypes = types.Select(item => new
            {
                typeId = item.GetProperty("typeId").GetString(),
                typeKey = item.GetProperty("key").GetString(),
                name = item.GetProperty("name").GetString(),
                description = item.GetProperty("description").GetString(),
                isEnabled = true,
                sortOrder = item.GetProperty("sortOrder").GetInt32(),
                properties = Array.Empty<object>()
            }),
            templates = Array.Empty<object>(),
            relationTypes = relationTypes.Select(item => new
            {
                relationTypeId = item.GetProperty("relationTypeId").GetString(),
                relationKey = item.GetProperty("key").GetString(),
                name = item.GetProperty("name").GetString(),
                description = item.GetProperty("description").GetString(),
                isDirected = item.GetProperty("isDirected").GetBoolean(),
                isEnabled = true,
                sortOrder = item.GetProperty("sortOrder").GetInt32()
            }),
            sourceRules = sourceRules.Select(item => new
            {
                sourceRuleId = item.GetProperty("sourceRuleId").GetString(),
                sourceKind = item.GetProperty("kind").GetString(),
                metadataSchema = item.GetProperty("metadataSchema"),
                supportedLocatorTypes = item
                    .GetProperty("supportedLocatorTypes")
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .ToArray(),
                isEnabled = true
            }),
            workflows = Array.Empty<object>()
        };
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/knowledge-model")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-CSRF-TOKEN", csrf);

        using var put = await server.Client.SendAsync(request);
        var putBody = await put.Content.ReadAsStringAsync();
        Assert.True(
            put.StatusCode == HttpStatusCode.OK,
            $"PUT failed: {(int)put.StatusCode} {putBody}{Environment.NewLine}{string.Join(Environment.NewLine, logs.Messages)}");
        Assert.Equal("\"2\"", put.Headers.ETag?.Tag);
    }
}
