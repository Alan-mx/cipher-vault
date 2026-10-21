using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.ImportExport;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class ImportApplyEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ImportApplyCommitsPreviewedFiles()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var sourceRoot = Path.Combine(fixture.Root, "apply-source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "applied.md"),
            "# Applied\nimport applied marker");
        var preview = await PreviewAsync(fixture.Client, sourceRoot);
        using var request = CreateRequest(preview.JobId);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultImportResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(preview.JobId, payload.Data.JobId);
        Assert.Equal(1, payload.Data.Imported);
        Assert.Equal("completed", payload.Data.State);

        using var catalogRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/catalog");
        catalogRequest.Headers.Host = "127.0.0.1:43127";
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var catalogResponse = await fixture.Client.SendAsync(catalogRequest);
        catalogResponse.EnsureSuccessStatusCode();
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<
            HostApiResponse<VaultCatalogPage>>(JsonOptions);
        Assert.NotNull(catalog);
        Assert.Contains(
            catalog.Data.Items,
            item => string.Equals(
                item.VirtualPath,
                "applied.md",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportApplyRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest(Guid.NewGuid());
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

    private static async Task<VaultImportPreview> PreviewAsync(
        HttpClient client,
        string sourceRoot)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/import/preview")
        {
            Content = JsonContent.Create(
                new HostImportPreviewRequest(sourceRoot))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultImportPreview>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data;
    }

    private static HttpRequestMessage CreateRequest(Guid jobId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/v1/import/{jobId:D}/apply");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
