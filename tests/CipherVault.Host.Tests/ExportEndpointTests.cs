using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.ImportExport;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class ExportEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExportWritesPlaintextSnapshot()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await fixture.CreateNoteAsync(
            "Export note",
            "export marker 9f98",
            folder: "inbox");
        await UnlockHostAsync(fixture.Client);
        var destination = Path.Combine(fixture.Root, "export");
        using var request = CreateRequest(destination);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultExportResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(1, payload.Data.ExportedDocuments);
        Assert.True(File.Exists(payload.Data.ManifestPath));
        Assert.True(payload.Data.AuditRecorded);
        var document = Assert.Single(
            Directory.GetFiles(destination, "*.md", SearchOption.AllDirectories));
        Assert.Contains("export marker 9f98", await File.ReadAllTextAsync(document));
    }

    [Fact]
    public async Task ExportRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var destination = Path.Combine(fixture.Root, "locked-export");
        using var request = CreateRequest(destination);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("session_locked", payload.Error.Code);
        Assert.False(Directory.Exists(destination));
    }

    private static async Task UnlockHostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/unlock")
        {
            Content = JsonContent.Create(new HostUnlockRequest("password"))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(string destinationPath)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/export")
        {
            Content = JsonContent.Create(new HostExportRequest(destinationPath))
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
