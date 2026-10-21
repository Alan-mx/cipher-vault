using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.ImportExport;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class ImportPreviewEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ImportPreviewScansSupportedFiles()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var sourceRoot = Path.Combine(fixture.Root, "import-source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "preview.md"),
            "# Preview\nimport marker");
        using var request = CreateRequest(sourceRoot);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultImportPreview>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.NotEqual(Guid.Empty, payload.Data.JobId);
        Assert.Equal(1, payload.Data.TotalFiles);
        Assert.Equal(1, payload.Data.SupportedFiles);
        var item = Assert.Single(payload.Data.Items);
        Assert.Equal("preview.md", item.RelativePath);
        Assert.Equal("md", item.Format);
    }

    [Fact]
    public async Task ImportPreviewRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.Root, "locked-import");
        Directory.CreateDirectory(sourceRoot);
        using var request = CreateRequest(sourceRoot);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("session_locked", payload.Error.Code);
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

    private static HttpRequestMessage CreateRequest(string sourceRoot)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/import/preview")
        {
            Content = JsonContent.Create(
                new HostImportPreviewRequest(sourceRoot))
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
