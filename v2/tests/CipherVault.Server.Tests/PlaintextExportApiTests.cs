using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Exports;

namespace CipherVault.Server.Tests;

public sealed class PlaintextExportApiTests
{
    [Fact]
    public async Task ExportsZipWithCsrfFormAndRecordsAudit()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var csrf = await server.Client.GetAntiforgeryTokenAsync();
        using (var created = await server.Client.PostJsonWithAntiforgeryAsync(
            "/api/documents",
            csrf,
            new
            {
                title = "Exported",
                folder = "notes",
                format = "md",
                content = "# Exported\n正文",
                tags = new[] { "export" }
            }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var wrong = await PostExportAsync(
            server.Client,
            csrf,
            "错误确认");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var wrongJson = JsonDocument.Parse(
            await wrong.Content.ReadAsStringAsync());
        Assert.Equal(
            "invalid_input",
            wrongJson.RootElement.GetProperty("code").GetString());

        using var response = await PostExportAsync(
            server.Client,
            csrf,
            PlaintextExportLimits.ConfirmationPhrase);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("none", response.Headers.AcceptRanges.ToString());
        Assert.Matches(
            "attachment; filename=cipher-vault-export-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\\.zip",
            response.Content.Headers.ContentDisposition?.ToString()
                ?? string.Empty);

        using var zipStream = new MemoryStream(
            await response.Content.ReadAsByteArrayAsync());
        using var archive = new ZipArchive(
            zipStream,
            ZipArchiveMode.Read,
            leaveOpen: false);
        Assert.Equal(
            ["notes/Exported.md", PlaintextExportLimits.ManifestFileName],
            archive.Entries.Select(entry => entry.FullName));
        Assert.Equal(
            "# Exported\n正文",
            ReadEntry(archive, "notes/Exported.md"));
        using var manifest = JsonDocument.Parse(
            ReadEntry(archive, PlaintextExportLimits.ManifestFileName));
        Assert.Equal(
            1,
            manifest.RootElement.GetProperty("documentCount").GetInt32());

        using var audit = await server.Client.GetAsync(
            "/api/audit?action=export.plaintext");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var auditJson = JsonDocument.Parse(
            await audit.Content.ReadAsStringAsync());
        Assert.Single(
            auditJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task RequiresAuthenticationAndCsrf()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var anonymous = server.CreateClient();
        using var form = CreateExportForm("unused", PlaintextExportLimits.ConfirmationPhrase);
        using var unauthorized = await anonymous.PostAsync(
            "/api/exports/plaintext",
            form);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        using var missingCsrf = await server.Client.PostAsync(
            "/api/exports/plaintext",
            CreateExportForm(
                token: null,
                PlaintextExportLimits.ConfirmationPhrase));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
    }

    [Fact]
    public async Task EmptyVaultReturnsManifestOnlyZip()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        var csrf = await server.Client.GetAntiforgeryTokenAsync();

        using var response = await PostExportAsync(
            server.Client,
            csrf,
            PlaintextExportLimits.ConfirmationPhrase);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var archive = new ZipArchive(
            new MemoryStream(await response.Content.ReadAsByteArrayAsync()),
            ZipArchiveMode.Read);
        Assert.Single(archive.Entries);
        Assert.Equal(
            PlaintextExportLimits.ManifestFileName,
            archive.Entries[0].FullName);
    }

    private static async Task<HttpResponseMessage> PostExportAsync(
        HttpClient client,
        string csrf,
        string confirmation) =>
        await client.PostAsync(
            "/api/exports/plaintext",
            CreateExportForm(csrf, confirmation));

    private static FormUrlEncodedContent CreateExportForm(
        string? token,
        string confirmation)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("confirmation", confirmation)
        };
        if (token is not null)
        {
            values.Add(
                new KeyValuePair<string, string>(
                    "__CipherVaultAntiforgery",
                    token));
        }
        return new FormUrlEncodedContent(values);
    }

    private static string ReadEntry(
        ZipArchive archive,
        string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidOperationException($"Missing entry: {name}");
        using var reader = new StreamReader(
            entry.Open(),
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}
