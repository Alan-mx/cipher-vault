using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api.Contracts;

namespace CipherVault.Server.Tests;

public sealed class TrashApiTests
{
    [Fact]
    public async Task TrashRestoreAndEligiblePurgeAreExposed()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        var documentId = await CreateAsync(server.Client, token);
        var path = $"/api/documents/{documentId:D}";

        using var deleted = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Delete,
            path,
            token,
            revision: 1);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        using var ineligible = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Post,
            $"/api/trash/{documentId:D}/purge",
            token,
            revision: 2);
        Assert.Equal(HttpStatusCode.Conflict, ineligible.StatusCode);
        using var ineligibleJson = JsonDocument.Parse(
            await ineligible.Content.ReadAsStringAsync());
        Assert.Equal(
            "purge_not_eligible",
            ineligibleJson.RootElement.GetProperty("code").GetString());

        using var trash = await server.Client.GetAsync("/api/trash");
        using var trashJson = JsonDocument.Parse(
            await trash.Content.ReadAsStringAsync());
        Assert.Single(trashJson.RootElement.GetProperty("items").EnumerateArray());

        using var restored = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Post,
            $"/api/trash/{documentId:D}/restore",
            token,
            revision: 2);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("\"3\"", restored.Headers.ETag?.ToString());

        using var deletedAgain = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Delete,
            path,
            token,
            revision: 3);
        Assert.Equal(HttpStatusCode.OK, deletedAgain.StatusCode);

        server.TimeProvider.MoveWallClock(TimeSpan.FromDays(31));
        using var reauth = await server.Client.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));
        Assert.Equal(HttpStatusCode.OK, reauth.StatusCode);
        token = await server.Client.GetAntiforgeryTokenAsync();
        using var purged = await server.Client.SendWithRevisionAsync<object>(
            HttpMethod.Post,
            $"/api/trash/{documentId:D}/purge",
            token,
            revision: 4);
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        using var purgeJson = JsonDocument.Parse(
            await purged.Content.ReadAsStringAsync());
        Assert.NotEqual(
            JsonValueKind.Null,
            purgeJson.RootElement.GetProperty("auditEventId").ValueKind);

        using var emptyTrash = await server.Client.GetAsync("/api/trash");
        using var emptyJson = JsonDocument.Parse(
            await emptyTrash.Content.ReadAsStringAsync());
        Assert.Empty(emptyJson.RootElement.GetProperty("items").EnumerateArray());
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        string token)
    {
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Trash API",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("documentId").GetGuid();
    }
}
