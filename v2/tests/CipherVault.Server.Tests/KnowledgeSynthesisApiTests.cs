using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeSynthesisApiTests
{
    private const string Path = "/api/knowledge/synthesis-draft";
    private static readonly Guid ArticleTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001002");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CookieCanCreateAndReadSynthesisDraft()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var first = await CreateKnowledgeAsync(server, "First");
        var second = await CreateKnowledgeAsync(server, "Second");

        using var response = await server.Client.PostJsonWithAntiforgeryAsync(
            Path,
            csrf,
            new
            {
                topic = "Cross-source synthesis",
                question = "Which claims overlap?",
                knowledgeIds = new[]
                {
                    second.ObjectId.ToString("D"),
                    first.ObjectId.ToString("D")
                },
                knowledgeTypeId = ArticleTypeId.ToString("D")
            },
            Guid.NewGuid());
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{(int)response.StatusCode}: {body}");
        using var created = JsonDocument.Parse(body);
        var draftId = created.RootElement.GetProperty("draftId").GetGuid();
        Assert.Equal(
            "/api/drafts/" + draftId.ToString("D"),
            response.Headers.Location!.OriginalString);

        using var detailResponse = await server.Client.GetAsync(
            $"/api/drafts/{draftId:D}");
        using var detail = JsonDocument.Parse(
            await detailResponse.Content.ReadAsStringAsync());
        var payload = detail.RootElement.GetProperty("record")
            .GetProperty("payload");
        Assert.Equal(
            "Cross-source synthesis · 知识综合",
            payload.GetProperty("title").GetString());
        Assert.Equal(
            ArticleTypeId,
            payload.GetProperty("knowledgeTypeId").GetGuid());
        var content = payload.GetProperty("contentMarkdown").GetString()!;
        Assert.Contains("## 一、综合问题与范围", content, StringComparison.Ordinal);
        Assert.Contains("Which claims overlap?", content, StringComparison.Ordinal);
        Assert.Contains("## 七、关联知识", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf(second.ObjectId.ToString("D"), StringComparison.Ordinal)
            < content.IndexOf(first.ObjectId.ToString("D"), StringComparison.Ordinal));

        var audit = await ReadDraftCreateAuditDetailsAsync(server, draftId);
        Assert.DoesNotContain(
            "Which claims overlap?",
            audit,
            StringComparison.Ordinal);
        Assert.DoesNotContain("综合结论", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdempotencyIsStableAndRejectsDifferentRequest()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var knowledge = await CreateKnowledgeAsync(server, "Knowledge");
        var key = Guid.NewGuid();
        var request = new
        {
            topic = "Synthesis",
            question = "Compare",
            knowledgeIds = new[] { knowledge.ObjectId.ToString("D") },
            knowledgeTypeId = ArticleTypeId.ToString("D")
        };

        using var first = await server.Client.PostJsonWithAntiforgeryAsync(
            Path,
            csrf,
            request,
            key);
        using var replay = await server.Client.PostJsonWithAntiforgeryAsync(
            Path,
            csrf,
            request,
            key);
        using var firstJson = JsonDocument.Parse(
            await first.Content.ReadAsStringAsync());
        using var replayJson = JsonDocument.Parse(
            await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(
            firstJson.RootElement.GetProperty("draftId").GetGuid(),
            replayJson.RootElement.GetProperty("draftId").GetGuid());

        using var reused = await server.Client.PostJsonWithAntiforgeryAsync(
            Path,
            csrf,
            new
            {
                topic = "Different",
                question = request.question,
                knowledgeIds = request.knowledgeIds,
                knowledgeTypeId = request.knowledgeTypeId
            },
            key);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
    }

    [Fact]
    public async Task McpBearerIsForbiddenBeforeCsrfValidation()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        using var mcpClient = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new
            {
                topic = "MCP",
                knowledgeIds = new[] { Guid.NewGuid().ToString("D") },
                knowledgeTypeId = ArticleTypeId.ToString("D")
            })
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var response = await mcpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("forbidden", body, StringComparison.Ordinal);
        Assert.DoesNotContain("antiforgery", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookieRequestWithoutCsrfIsRejected()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var knowledge = await CreateKnowledgeAsync(server, "Knowledge");
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new
            {
                topic = "No CSRF",
                knowledgeIds = new[] { knowledge.ObjectId.ToString("D") },
                knowledgeTypeId = ArticleTypeId.ToString("D")
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "antiforgery_invalid",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private static async Task<DraftApplyResult> CreateKnowledgeAsync(
        ServerTestApplication server,
        string title)
    {
        var drafts = new UnifiedDraftService(
            server.Manager,
            server.TimeProvider);
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var payload = JsonSerializer.SerializeToElement(
            new KnowledgeMutationPayload(
                title,
                string.Empty,
                ArticleTypeId,
                null,
                "# " + title,
                JsonSerializer.SerializeToElement(new { }),
                [],
                []),
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
        return await drafts.ApplyAsync(
            submitted.DraftId,
            submitted.DraftRevision,
            actor,
            Guid.NewGuid());
    }

    private static Task<string> ReadDraftCreateAuditDetailsAsync(
        ServerTestApplication server,
        Guid draftId) =>
        server.Manager.ExecuteAsync(
            async (context, token) =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT details_json
                    FROM audit_events
                    WHERE action = 'draft.create'
                      AND target_id = $draftId
                    ORDER BY occurred_at DESC
                    LIMIT 1;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$draftId",
                    draftId.ToString("D"));
                return await command.Command.ExecuteScalarAsync(token)
                    as string
                    ?? throw new InvalidOperationException(
                        "Draft create audit was not found.");
            }).AsTask();
}
