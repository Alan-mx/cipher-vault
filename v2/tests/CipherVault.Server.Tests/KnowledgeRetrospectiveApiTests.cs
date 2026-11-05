using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.Documents;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Tests;

public sealed class KnowledgeRetrospectiveApiTests
{
    private const string Path = "/api/knowledge/retrospective-draft";
    private static readonly Guid ReflectionTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001006");

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CookieCanCreateAndReadRetrospectiveDraft()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        var first = await CreateKnowledgeAsync(server, "First knowledge");
        var second = await CreateKnowledgeAsync(server, "Second knowledge");
        var request = new
        {
            topic = "Release review",
            knowledgeIds = new[]
            {
                second.ObjectId.ToString("D"),
                first.ObjectId.ToString("D")
            }
        };

        using var response = await server.Client
            .PostJsonWithAntiforgeryAsync(
                Path,
                csrf,
                request,
                Guid.NewGuid());
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{(int)response.StatusCode}: {body}");
        Assert.NotNull(response.Headers.Location);
        using var created = JsonDocument.Parse(body);
        var draftId = created.RootElement.GetProperty("draftId").GetGuid();
        Assert.Equal(
            "draft",
            created.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "/api/drafts/" + draftId.ToString("D"),
            response.Headers.Location!.OriginalString);

        using var detailResponse = await server.Client.GetAsync(
            $"/api/drafts/{draftId:D}");
        var detailBody = await detailResponse.Content.ReadAsStringAsync();
        Assert.True(
            detailResponse.StatusCode == HttpStatusCode.OK,
            $"{(int)detailResponse.StatusCode}: {detailBody}");
        using var detail = JsonDocument.Parse(detailBody);
        var record = detail.RootElement.GetProperty("record");
        Assert.Equal("knowledge", record.GetProperty("targetObjectType").GetString());
        Assert.Equal("create", record.GetProperty("operationType").GetString());
        var payload = record.GetProperty("payload");
        Assert.Equal(
            "Release review · 知识复盘",
            payload.GetProperty("title").GetString());
        var content = payload.GetProperty("contentMarkdown").GetString()!;
        Assert.Contains("## 目标与背景", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf(second.ObjectId.ToString("D"), StringComparison.Ordinal)
            < content.IndexOf(first.ObjectId.ToString("D"), StringComparison.Ordinal));

        var auditDetails = await ReadDraftCreateAuditDetailsAsync(
            server,
            draftId);
        using var audit = JsonDocument.Parse(auditDetails);
        Assert.Equal(
            "knowledge",
            audit.RootElement.GetProperty("targetObjectType").GetString());
        Assert.Equal(
            "create",
            audit.RootElement.GetProperty("operationType").GetString());
        Assert.False(
            audit.RootElement.GetProperty("hasSourceReferences").GetBoolean());
        Assert.DoesNotContain(
            "Release review",
            auditDetails,
            StringComparison.Ordinal);
        Assert.DoesNotContain("目标与背景", auditDetails, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameIdempotencyKeyReturnsSameDraft()
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
            topic = "Idempotent",
            knowledgeIds = new[] { knowledge.ObjectId.ToString("D") }
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
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var firstJson = JsonDocument.Parse(
            await first.Content.ReadAsStringAsync());
        using var replayJson = JsonDocument.Parse(
            await replay.Content.ReadAsStringAsync());
        Assert.Equal(
            firstJson.RootElement.GetProperty("draftId").GetGuid(),
            replayJson.RootElement.GetProperty("draftId").GetGuid());

        using var reused = await server.Client.PostJsonWithAntiforgeryAsync(
            Path,
            csrf,
            new
            {
                topic = "Different request",
                knowledgeIds = request.knowledgeIds
            },
            key);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Contains(
            "idempotency_key_reused",
            await reused.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpBearerIsForbiddenBeforeCsrfValidation()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await McpTestClient.CreateTokenAsync(server.Client);
        using var mcpClient = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new
            {
                topic = "MCP request",
                knowledgeIds = new[] { Guid.NewGuid().ToString("D") }
            })
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var response = await mcpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "forbidden",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "antiforgery",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
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
                knowledgeIds = new[] { knowledge.ObjectId.ToString("D") }
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
                ReflectionTypeId,
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
                var value = await command.Command.ExecuteScalarAsync(token);
                return value as string
                    ?? throw new InvalidOperationException(
                        "Draft create audit was not found.");
            }).AsTask();
}
