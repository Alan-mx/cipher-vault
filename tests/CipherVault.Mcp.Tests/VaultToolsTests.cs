using System.Text.Json;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Mcp.Tests;

public sealed class VaultToolsTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task ListCatalogPromptsAndUnlocks()
    {
        await using var fixture = await VaultToolsFixture.CreateAsync();
        var prompt = new FakePrompt(true, Password);
        var tools = new VaultTools(fixture.Service, fixture.VaultPath, prompt);

        var json = await tools.ListCatalog(cancellationToken: CancellationToken.None);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal(VaultSessionState.Unlocked, (await fixture.Service.GetStatusAsync()).State);
    }

    [Fact]
    public async Task DeniedPromptUsesCooldownWithoutRepeatedPrompt()
    {
        await using var fixture = await VaultToolsFixture.CreateAsync();
        var prompt = new FakePrompt(false, null);
        var tools = new VaultTools(fixture.Service, fixture.VaultPath, prompt);

        var first = await tools.ListCatalog(cancellationToken: CancellationToken.None);
        var second = await tools.ListCatalog(cancellationToken: CancellationToken.None);
        using var firstDocument = JsonDocument.Parse(first);
        using var secondDocument = JsonDocument.Parse(second);

        Assert.Equal("auth_denied", firstDocument.RootElement
            .GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("auth_denied", secondDocument.RootElement
            .GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, prompt.CallCount);
        Assert.True(secondDocument.RootElement
            .GetProperty("error").TryGetProperty("retryAfterMs", out _));
    }

    [Fact]
    public async Task ContentToolsRunEndToEndWithOneAuthorization()
    {
        await using var fixture = await VaultToolsFixture.CreateAsync();
        var prompt = new FakePrompt(true, Password);
        var tools = new VaultTools(fixture.Service, fixture.VaultPath, prompt);

        using var createdDocument = JsonDocument.Parse(
            await tools.CreateNote(
                "MCP 文档",
                "独特 MCP 内容",
                folder: "测试",
                format: "md",
                tags: ["mcp"],
                cancellationToken: CancellationToken.None));
        var documentId = createdDocument.RootElement
            .GetProperty("document")
            .GetProperty("documentId")
            .GetGuid();
        using var searchDocument = JsonDocument.Parse(
            await tools.SearchNotes(
                "独特 MCP",
                cancellationToken: CancellationToken.None));
        using var readDocument = JsonDocument.Parse(
            await tools.ReadNote(
                documentId.ToString("D"),
                cancellationToken: CancellationToken.None));
        using var updatedDocument = JsonDocument.Parse(
            await tools.UpdateNote(
                documentId.ToString("D"),
                1,
                content: "更新后的 MCP 内容",
                cancellationToken: CancellationToken.None));
        using var deletedDocument = JsonDocument.Parse(
            await tools.DeleteNote(
                documentId.ToString("D"),
                2,
                CancellationToken.None));

        Assert.Single(searchDocument.RootElement
            .GetProperty("page")
            .GetProperty("hits")
            .EnumerateArray());
        Assert.Contains(
            "独特 MCP 内容",
            readDocument.RootElement.GetProperty("document").GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(2, updatedDocument.RootElement
            .GetProperty("document")
            .GetProperty("revision")
            .GetInt32());
        Assert.True(deletedDocument.RootElement
            .GetProperty("document")
            .GetProperty("purgeAfter")
            .GetDateTime() > DateTime.UtcNow);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public void WindowsActivityProviderRecognizesInteractiveUnlockedSession()
    {
        var snapshot = new WindowsSystemActivityProvider().GetSnapshot();

        Assert.False(snapshot.IsWorkstationLocked);
        Assert.True(snapshot.SystemIdleTime >= TimeSpan.Zero);
    }

    private sealed class FakePrompt : ICredentialPrompt
    {
        private readonly bool _confirmed;
        private readonly string? _password;

        public FakePrompt(bool confirmed, string? password)
        {
            _confirmed = confirmed;
            _password = password;
        }

        public int CallCount { get; private set; }

        public Task<PasswordPromptResult> RequestAsync(
            string title,
            string message,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new PasswordPromptResult(_confirmed, _password));
        }
    }

    private sealed class VaultToolsFixture : IAsyncDisposable
    {
        private VaultToolsFixture(string rootPath, string vaultPath, VaultService service)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            Service = service;
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public VaultService Service { get; }

        public static async Task<VaultToolsFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-mcp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var service = new VaultService(new AlwaysActiveActivityProvider());
            var initialized = await service.InitializeAsync(vaultPath, Password);
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            return new VaultToolsFixture(root, vaultPath, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
