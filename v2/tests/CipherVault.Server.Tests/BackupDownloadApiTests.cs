using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CipherVault.Server.Tests;

public sealed class BackupDownloadApiTests
{
    [Fact]
    public async Task DownloadStreamsAttachmentAndRecordsAudit()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var antiforgery = await server.Client.GetAntiforgeryTokenAsync();
        using var created = await server.Client.PostWithAntiforgeryAsync(
            "/api/backups",
            antiforgery);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var fileName = createdJson.RootElement
            .GetProperty("backup")
            .GetProperty("fileName")
            .GetString()!;
        var path = Path.Combine(
            server.Options.Paths.BackupDirectory,
            fileName);
        var expectedBytes = await File.ReadAllBytesAsync(path);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/backups/{fileName}/download");
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await server.Client.SendAsync(request);
        var actualBytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedBytes, actualBytes);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("none", response.Headers.AcceptRanges.Single());
        Assert.Equal(
            "application/octet-stream",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "attachment",
            response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(expectedBytes.Length, response.Content.Headers.ContentLength);

        using var audit = await server.Client.GetAsync(
            "/api/audit?action=backup.download");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var auditJson = JsonDocument.Parse(
            await audit.Content.ReadAsStringAsync());
        var items = auditJson.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, item =>
        {
            Assert.Equal(
                "backup.download",
                item.GetProperty("action").GetString());
            Assert.Equal("user_ui", item.GetProperty("actor").GetString());
            Assert.Equal(
                "backup",
                item.GetProperty("targetObjectType").GetString());
        });
        Assert.Equal(
            ["started", "success"],
            items
                .Select(item => item.GetProperty("result").GetString())
                .Order(StringComparer.Ordinal));
        var downloadIds = items
            .Select(item =>
                JsonDocument.Parse(
                    item.GetProperty("detailsJson").GetString()!)
                    .RootElement.GetProperty("downloadId").GetString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(downloadIds);
        Assert.DoesNotContain(
            server.Options.Paths.BackupDirectory,
            items[0].GetProperty("detailsJson").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadRejectsInvalidOrUnknownFilename()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            unlockVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);

        using var invalid = await server.Client.GetAsync(
            "/api/backups/cipher-vault-20261101-000000-DEADBEEF.cvbackup/download");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var missing = await server.Client.GetAsync(
            "/api/backups/cipher-vault-20261101-000000-deadbeef.cvbackup/download");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains(
            "backup_not_found",
            await missing.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }
}
