using System.Net;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Tests;

public sealed class AuditApiTests
{
    [Fact]
    public async Task AuditQuerySupportsFiltersAndRequiresAuthentication()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var anonymousClient = server.CreateClient();
        using var unauthorized = await anonymousClient.GetAsync("/api/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await server.Client.GetAntiforgeryTokenAsync();
        using var created = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            token,
            new CreateDocumentRequest(
                "Audit API",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var documentId = createdJson.RootElement
            .GetProperty("documentId")
            .GetGuid();

        using var audit = await server.Client.GetAsync(
            $"/api/audit?actor=user_ui&action=create&documentId={documentId:D}&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var auditJson = JsonDocument.Parse(
            await audit.Content.ReadAsStringAsync());
        var items = auditJson.RootElement.GetProperty("items").EnumerateArray()
            .ToArray();
        Assert.Single(items);
        Assert.Equal("create", items[0].GetProperty("action").GetString());
        Assert.DoesNotContain(
            "body",
            items[0].GetProperty("detailsJson").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditRejectsInvalidActor()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var response = await server.Client.GetAsync(
            "/api/audit?actor=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
