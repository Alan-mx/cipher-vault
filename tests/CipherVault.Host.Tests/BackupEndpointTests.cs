using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Tests;

public sealed class BackupEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BackupCreatesArchive()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await fixture.CreateNoteAsync("Backup note", "backup content");
        await UnlockHostAsync(fixture.Client);
        var backupPath = Path.Combine(fixture.Root, "backup.cvbackup");
        using var request = CreateRequest(backupPath);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultBackupResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(Path.GetFullPath(backupPath), payload.Data.BackupPath);
        Assert.True(File.Exists(backupPath));
        Assert.True(new FileInfo(backupPath).Length > 0);
        Assert.True(payload.Data.AuditRecorded);
    }

    [Fact]
    public async Task BackupRequiresUnlockedVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var backupPath = Path.Combine(fixture.Root, "locked.cvbackup");
        using var request = CreateRequest(backupPath);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("session_locked", payload.Error.Code);
        Assert.False(File.Exists(backupPath));
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
            "/admin/v1/maintenance/backup")
        {
            Content = JsonContent.Create(new HostBackupRequest(destinationPath))
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }
}
