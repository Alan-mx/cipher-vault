using System.Net;
using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeTriageApiTests
{
    private static readonly Guid NoteTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001001");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TriageReturnsMissingSourceAndCursor()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        await CreateKnowledgeAsync(server, "First triage");
        await CreateKnowledgeAsync(server, "Second triage");

        using var first = await server.Client.GetAsync(
            "/api/knowledge/triage?reason=missing_source&pageSize=1");
        var firstBody = await first.Content.ReadAsStringAsync();
        using var firstJson = JsonDocument.Parse(firstBody);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        var items = firstJson.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Contains(
            items[0].GetProperty("reasonCodes").EnumerateArray(),
            reason => reason.GetString() == "missing_source");
        Assert.Equal(
            2,
            firstJson.RootElement.GetProperty("counts")
                .GetProperty("totalDistinct")
                .GetInt32());
        var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        using var second = await server.Client.GetAsync(
            $"/api/knowledge/triage?reason=missing_source&pageSize=1&cursor={Uri.EscapeDataString(cursor)}");
        using var secondJson = JsonDocument.Parse(
            await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(
            items[0].GetProperty("knowledgeId").GetString(),
            secondJson.RootElement.GetProperty("items")[0]
                .GetProperty("knowledgeId")
                .GetString());
    }

    [Fact]
    public async Task TriageRejectsInvalidFilters()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();

        using var reason = await server.Client.GetAsync(
            "/api/knowledge/triage?reason=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, reason.StatusCode);
        Assert.Contains(
            "invalid_input",
            await reason.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var staleDays = await server.Client.GetAsync(
            "/api/knowledge/triage?staleDays=14");
        Assert.Equal(HttpStatusCode.BadRequest, staleDays.StatusCode);

        using var pageSize = await server.Client.GetAsync(
            "/api/knowledge/triage?pageSize=51");
        Assert.Equal(HttpStatusCode.BadRequest, pageSize.StatusCode);
        Assert.Contains(
            "invalid_page_size",
            await pageSize.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var cursor = await server.Client.GetAsync(
            "/api/knowledge/triage?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, cursor.StatusCode);
        Assert.Contains(
            "invalid_cursor",
            await cursor.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriageRequiresCookieSession()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var client = server.CreateClient();

        using var response = await client.GetAsync("/api/knowledge/triage");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            "authentication_required",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private static async Task CreateKnowledgeAsync(
        ServerTestApplication server,
        string title)
    {
        var drafts = new UnifiedDraftService(
            server.Manager,
            server.TimeProvider);
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                title,
                summary = "",
                knowledgeTypeId = NoteTypeId,
                templateId = (string?)null,
                contentMarkdown = "# " + title,
                properties = new { },
                tags = Array.Empty<string>(),
                sourceReferences = Array.Empty<object>()
            },
            JsonOptions);
        var created = await drafts.CreateAsync(
            new CreateDraftInput(
                DraftTargetObjectType.Knowledge,
                DraftOperationType.Create,
                null,
                null,
                payload),
            actor,
            Guid.NewGuid());
        var submitted = await drafts.SubmitAsync(
            created.DraftId,
            created.DraftRevision,
            actor,
            Guid.NewGuid());
        _ = await drafts.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            actor,
            Guid.NewGuid());
    }
}
