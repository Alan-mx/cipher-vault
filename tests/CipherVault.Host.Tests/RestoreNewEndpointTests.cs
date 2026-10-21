using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core;
using CipherVault.Core.Backup;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class RestoreNewEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RestoreNewCreatesIndependentVault()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await fixture.CreateNoteAsync("Restore marker", "restored content");
        await UnlockHostAsync(fixture.Client);
        var backupPath = await CreateBackupAsync(fixture);
        var targetPath = Path.Combine(fixture.Root, "restored-vault");
        using var request = CreateRequest(backupPath, targetPath);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultRestoreBackupResult>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(Path.GetFullPath(targetPath), payload.Data.VaultPath);
        await using var restored = new VaultService(
            new AlwaysActiveActivityProvider());
        await restored.UnlockAsync(
            targetPath,
            HostTestFixture.VaultPassword,
            VaultCredentialType.Password);
        var catalog = await restored.ListCatalogAsync(null, null, 10);
        Assert.Equal("Restore marker", Assert.Single(catalog.Items).Title);
    }

    [Fact]
    public async Task RestoreNewRejectsExistingTarget()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var backupPath = await CreateBackupAsync(fixture);
        var targetPath = Path.Combine(fixture.Root, "existing-target");
        Directory.CreateDirectory(targetPath);
        var promptCount = fixture.CredentialPrompt.RequestCount;
        using var request = CreateRequest(backupPath, targetPath);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("already_exists", payload.Error.Code);
        Assert.Equal(promptCount, fixture.CredentialPrompt.RequestCount);
    }

    [Fact]
    public async Task RestoreNewRejectsCancelledAuthorization()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var backupPath = await CreateBackupAsync(fixture);
        var targetPath = Path.Combine(fixture.Root, "cancelled-restore");
        fixture.CredentialPrompt.Result = new HostCredentialPromptResult(
            false,
            null,
            VaultCredentialType.Password);
        using var request = CreateRequest(backupPath, targetPath);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("access_denied", payload.Error.Code);
        Assert.False(Directory.Exists(targetPath));
    }

    [Fact]
    public async Task RestoreNewRejectsInvalidCredential()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var backupPath = await CreateBackupAsync(fixture);
        var targetPath = Path.Combine(fixture.Root, "invalid-credential-restore");
        fixture.CredentialPrompt.Result = new HostCredentialPromptResult(
            true,
            "wrong backup password",
            VaultCredentialType.Password);
        using var request = CreateRequest(backupPath, targetPath);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_credential", payload.Error.Code);
        Assert.False(Directory.Exists(targetPath));
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

    private static async Task<string> CreateBackupAsync(HostTestFixture fixture)
    {
        var backupPath = Path.Combine(fixture.Root, $"backup-{Guid.NewGuid():N}.cvbackup");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/maintenance/backup")
        {
            Content = JsonContent.Create(new HostBackupRequest(backupPath))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return backupPath;
    }

    private static HttpRequestMessage CreateRequest(
        string backupPath,
        string targetPath)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/maintenance/restore-new")
        {
            Content = JsonContent.Create(
                new HostRestoreNewRequest(backupPath, targetPath))
        };
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
