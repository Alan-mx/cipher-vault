using System.IO;
using System.Net.Http;
using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CipherVault.Host.Tests;

internal sealed class HostTestFixture : IAsyncDisposable
{
    public const string AdminToken = "test-admin-token";
    public const string VaultPassword = "host integration password";
    public const string BaseUrl = "http://127.0.0.1:43127";

    private HostTestFixture(
        string root,
        string vaultPath,
        string configPath,
        string? recoveryKey,
        WebApplication app,
        HttpClient client,
        TestCredentialPrompt credentialPrompt)
    {
        Root = root;
        VaultPath = vaultPath;
        ConfigPath = configPath;
        RecoveryKey = recoveryKey;
        App = app;
        Client = client;
        CredentialPrompt = credentialPrompt;
    }

    public string Root { get; }
    public string VaultPath { get; }
    public string ConfigPath { get; }
    public string? RecoveryKey { get; }
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public TestCredentialPrompt CredentialPrompt { get; }
    public IHostApplicationLifetime Lifetime =>
        App.Services.GetRequiredService<IHostApplicationLifetime>();

    public async Task<VaultWriteResult> CreateNoteAsync(
        string title,
        string content,
        string? folder = null)
    {
        await using var writer = new VaultService(new AlwaysActiveActivityProvider());
        await writer.UnlockAsync(
            VaultPath,
            VaultPassword,
            VaultCredentialType.Password);
        return await writer.CreateNoteAsync(
            title,
            folder,
            "md",
            content,
            tags: null,
            VaultActorKind.UserUi);
    }

    public static async Task<HostTestFixture> CreateAsync(
        string[]? allowedOrigins = null,
        bool initializeVault = true,
        IReadOnlyList<AgentClientPolicy>? clients = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-host-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        string? recoveryKey = null;
        if (initializeVault)
        {
            await using var initializer = new VaultService(
                new AlwaysActiveActivityProvider());
            var initialized = await initializer.InitializeAsync(vaultPath, VaultPassword);
            recoveryKey = initialized.RecoveryKey;
            await initializer.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var configPath = Path.Combine(root, "config.json");
        var configuration = new ResolvedCipherVaultHostConfig(
            vaultPath,
            VaultSessionOptions.Default,
            new HostEndpointOptions(
                "127.0.0.1",
                43127,
                "/mcp",
                "/admin/v1",
                4096,
                new HashSet<string>(
                    allowedOrigins ?? [],
                    StringComparer.OrdinalIgnoreCase)),
            clients ?? [],
            configPath);
        var credentialPrompt = new TestCredentialPrompt();
        var app = HostApplication.Configure(
            builder,
            configuration,
            new TestAdminTokenStore(AdminToken),
            credentialPrompt,
            new AlwaysActiveActivityProvider());
        await app.StartAsync();
        return new HostTestFixture(
            root,
            vaultPath,
            configPath,
            recoveryKey,
            app,
            app.GetTestClient(),
            credentialPrompt);
    }

    public static HttpRequestMessage CreateRequest(
        string path = "/admin/v1/health")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "127.0.0.1:43127";
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    internal sealed class TestCredentialPrompt : IHostCredentialPrompt
    {
        public int RequestCount { get; private set; }
        public HostCredentialPreference? LastPreference { get; private set; }
        public string? LastMessage { get; private set; }
        public HostCredentialPromptResult Result { get; set; } = new(
            true,
            VaultPassword,
            VaultCredentialType.Password);
        private readonly Queue<HostCredentialPromptResult> _results = new();

        public void EnqueueResult(HostCredentialPromptResult result)
        {
            _results.Enqueue(result);
        }

        public Task<HostCredentialPromptResult> RequestAsync(
            HostCredentialPreference preference,
            string? message = null,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            LastPreference = preference;
            LastMessage = message;
            return Task.FromResult(
                _results.Count > 0 ? _results.Dequeue() : Result);
        }
    }

    private sealed class TestAdminTokenStore(string token) : IAdminTokenStore
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(token);
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
