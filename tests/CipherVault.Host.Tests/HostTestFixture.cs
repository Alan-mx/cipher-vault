using System.IO;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Security;
using CipherVault.Host.Session;
using Microsoft.Extensions.DependencyInjection;

namespace CipherVault.Host.Tests;

internal sealed class HostTestFixture : IAsyncDisposable
{
    public const string VaultPassword = "host integration password";

    private HostTestFixture(
        string root,
        string vaultPath,
        string? recoveryKey,
        HostTestApp app,
        TestCredentialPrompt credentialPrompt,
        TestAccessGrantPrompt accessGrantPrompt)
    {
        Root = root;
        VaultPath = vaultPath;
        RecoveryKey = recoveryKey;
        App = app;
        CredentialPrompt = credentialPrompt;
        AccessGrantPrompt = accessGrantPrompt;
    }

    public string Root { get; }
    public string VaultPath { get; }
    public string? RecoveryKey { get; }
    public HostTestApp App { get; }
    public TestCredentialPrompt CredentialPrompt { get; }
    public TestAccessGrantPrompt AccessGrantPrompt { get; }

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

    public static async Task<HostTestFixture> CreateAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-host-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        VaultInitializeResult initialized;
        await using (var initializer = new VaultService(
                         new AlwaysActiveActivityProvider()))
        {
            initialized = await initializer.InitializeAsync(vaultPath, VaultPassword);
            await initializer.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
        }

        var activity = new AlwaysActiveActivityProvider();
        var vault = new VaultService(activity);
        var credentialPrompt = new TestCredentialPrompt();
        var accessGrantPrompt = new TestAccessGrantPrompt();
        var securityLogDirectory = Path.Combine(root, "logs");
        var approvals = new ApprovalStore();
        var adminSessions = new AdminSessionStore();
        var accessGrants = new AccessGrantService(
            securityLog: new JsonlSecurityEventLog(securityLogDirectory));
        var sessions = new HostSessionCoordinator(
            vault,
            vaultPath,
            credentialPrompt,
            accessGrants,
            approvals,
            adminSessions);
        var services = new ServiceCollection()
            .AddSingleton(activity)
            .AddSingleton(vault)
            .AddSingleton<IVaultService>(vault)
            .AddSingleton(credentialPrompt)
            .AddSingleton<IHostCredentialPrompt>(credentialPrompt)
            .AddSingleton(accessGrantPrompt)
            .AddSingleton<IHostAccessGrantPrompt>(accessGrantPrompt)
            .AddSingleton(approvals)
            .AddSingleton(adminSessions)
            .AddSingleton(accessGrants)
            .AddSingleton(sessions)
            .AddSingleton<HostVaultGateway>()
            .BuildServiceProvider();
        var app = new HostTestApp(services);

        return new HostTestFixture(
            root,
            vaultPath,
            initialized.RecoveryKey,
            app,
            credentialPrompt,
            accessGrantPrompt);
    }

    public async ValueTask DisposeAsync()
    {
        var vault = App.Services.GetRequiredService<IVaultService>();
        await vault.DisposeAsync();
        if (App.Services is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
        if (Directory.Exists(Root))
        {
            await DeleteDirectoryWithRetryAsync(Root);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 49)
            {
                await Task.Delay(100);
            }
        }
    }

    internal sealed class HostTestApp
    {
        public HostTestApp(IServiceProvider services)
        {
            Services = services;
        }

        public IServiceProvider Services { get; }
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

    internal sealed class TestAccessGrantPrompt : IHostAccessGrantPrompt
    {
        public int RequestCount { get; private set; }
        public HostAccessGrantPromptRequest? LastRequest { get; private set; }
        public Func<
            HostAccessGrantPromptRequest,
            CancellationToken,
            Task<HostAccessGrantPromptResult>>? Handler
        { get; set; }
        public HostAccessGrantPromptResult Result { get; set; } = new(
            true,
            AccessGrantScope.None);
        private readonly Queue<HostAccessGrantPromptResult> _results = new();

        public void EnqueueResult(HostAccessGrantPromptResult result)
        {
            _results.Enqueue(result);
        }

        public Task<HostAccessGrantPromptResult> RequestAsync(
            HostAccessGrantPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            LastRequest = request;
            if (Handler is not null)
            {
                return Handler(request, cancellationToken);
            }
            var result = _results.Count > 0
                ? _results.Dequeue()
                : Result.Approved
                    ? Result with
                    {
                        ApprovedScopes = Result.ApprovedScopes == AccessGrantScope.None
                            ? request.RequestedScopes
                            : Result.ApprovedScopes
                    }
                    : Result;
            return Task.FromResult(result);
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
