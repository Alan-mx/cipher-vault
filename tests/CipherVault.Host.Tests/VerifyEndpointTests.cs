using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class VerifyEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task VerifyReturnsValidIntegrityReport()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await fixture.CreateNoteAsync("Verify note", "verified content");
        await UnlockHostAsync(fixture.Client);
        using var request = CreateRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultIntegrityReport>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.True(payload.Data.Valid);
        Assert.Equal(1, payload.Data.ActiveDocuments);
        Assert.Equal(1, payload.Data.Revisions);
        Assert.Equal(1, payload.Data.FtsRows);
        Assert.Empty(payload.Data.Problems);
    }

    [Fact]
    public async Task VerifyRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateRequest();
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

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/maintenance/verify");
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
