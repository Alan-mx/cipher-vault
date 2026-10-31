using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class DocumentLifecycleApiTests
{
    [Fact]
    public async Task DocumentReadUpdatePatchAndDeleteUseEtagAndCsrf()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();

        using var created = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Lifecycle",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "# Root\nOld",
                null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var documentId = createdJson.RootElement
            .GetProperty("documentId")
            .GetGuid();
        var path = $"/api/documents/{documentId:D}";

        using var metadata = await server.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal("\"1\"", metadata.Headers.ETag?.ToString());

        using var content = await server.Client.GetAsync(
            $"{path}/content?offset=0&maxChars=5");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        using var contentJson = JsonDocument.Parse(
            await content.Content.ReadAsStringAsync());
        Assert.False(contentJson.RootElement.GetProperty("done").GetBoolean());

        using var map = await server.Client.GetAsync($"{path}/map");
        Assert.Equal(HttpStatusCode.OK, map.StatusCode);
        Assert.Equal("\"1\"", map.Headers.ETag?.ToString());

        using var noOp = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            path,
            token,
            revision: 1,
            new UpdateDocumentRequest(
                "Lifecycle",
                null,
                "# Root\nOld",
                []));
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal("\"1\"", noOp.Headers.ETag?.ToString());

        using var missingEtag = await server.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, path)
            {
                Content = JsonContent.Create(
                    new UpdateDocumentRequest(
                        "Lifecycle",
                        null,
                        "# Root\nUpdated",
                        []))
            });
        Assert.Equal(HttpStatusCode.BadRequest, missingEtag.StatusCode);

        using var updated = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            path,
            token,
            revision: 1,
            new UpdateDocumentRequest(
                "Lifecycle",
                null,
                "# Root\nUpdated",
                []));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("\"2\"", updated.Headers.ETag?.ToString());

        using var conflict = await server.Client.SendWithRevisionAsync(
            HttpMethod.Put,
            path,
            token,
            revision: 1,
            new UpdateDocumentRequest(
                "Lifecycle",
                null,
                "# Root\nConflicting",
                []));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var conflictJson = JsonDocument.Parse(
            await conflict.Content.ReadAsStringAsync());
        Assert.Equal(
            2,
            conflictJson.RootElement
                .GetProperty("details")
                .GetProperty("currentRevision")
                .GetInt32());

        var dryRunCommand = new DocumentPatchCommand(
            VaultPatchTargetKind.Heading,
            ["Root"],
            null,
            VaultPatchOperation.Replace,
            "Preview",
            null,
            DryRun: true);
        using var dryRun = await server.Client.SendWithRevisionAsync(
            HttpMethod.Patch,
            path,
            token,
            revision: 2,
            dryRunCommand);
        Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        Assert.Equal("\"2\"", dryRun.Headers.ETag?.ToString());

        using var patched = await server.Client.SendWithRevisionAsync(
            HttpMethod.Patch,
            path,
            token,
            revision: 2,
            dryRunCommand with { DryRun = false });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("\"3\"", patched.Headers.ETag?.ToString());

        using var deleted = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Delete,
            path,
            token,
            revision: 3);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("\"4\"", deleted.Headers.ETag?.ToString());

        using var missing = await server.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
