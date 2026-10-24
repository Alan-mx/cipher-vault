using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Client;

namespace CipherVault.Ui.Wpf.Tests;

public sealed class ManualKnowledgeUiSmokeTests
{
    private const string Password = "manual-ui-smoke-password";
    private const string SaveMarker = "MANUAL_SAVE_CONFIRMED";

    [Fact]
    public async Task RunManualKnowledgeUiSmokeWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "CIPHER_VAULT_MANUAL_UI_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-manual-ui-{Guid.NewGuid():N}");
        var vaultPath = Path.Combine(root, "vault");
        Directory.CreateDirectory(root);
        try
        {
            await InitializeVaultAsync(vaultPath);
            await RunOnStaAsync(async () =>
            {
                var firstClient = await CreateUnlockedClientAsync(vaultPath);
                var target = await firstClient.CreateNoteAsync(
                    "目标",
                    null,
                    "md",
                    "# 目标",
                    null);
                var source = await firstClient.CreateNoteAsync(
                    "UI 验证",
                    null,
                    "md",
                    """
                    # UI 验证

                    [OpenAI 官网](https://openai.com)

                    [[目标]]

                    [[尚未创建的目标]]
                    """,
                    null);

                var firstWindow = new MainWindow(vaultPath, firstClient);
                firstWindow.Show();
                var workspace = Find<DocumentWorkspaceView>(
                    firstWindow,
                    "DocumentWorkspace");
                Assert.True(await workspace.LoadDocumentAsync(source.DocumentId));
                await WaitForWindowCloseAsync(firstWindow);

                var secondClient = await CreateUnlockedClientAsync(vaultPath);
                var secondWindow = new MainWindow(vaultPath, secondClient);
                secondWindow.Show();
                var secondWorkspace = Find<DocumentWorkspaceView>(
                    secondWindow,
                    "DocumentWorkspace");
                Assert.True(
                    await secondWorkspace.LoadDocumentAsync(source.DocumentId));
                await WaitForWindowCloseAsync(secondWindow);

                await using var verifier = new VaultService(
                    new AlwaysActiveActivityProvider());
                await verifier.UnlockAsync(
                    vaultPath,
                    Password,
                    VaultCredentialType.Password);
                var read = await verifier.ReadNoteAsync(
                    source.DocumentId,
                    0,
                    16_000);
                Assert.Contains(
                    SaveMarker,
                    read.Content,
                    StringComparison.Ordinal);
                Assert.NotEqual(Guid.Empty, target.DocumentId);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task InitializeVaultAsync(string vaultPath)
    {
        await using var service = new VaultService(
            new AlwaysActiveActivityProvider());
        var initialized = await service.InitializeAsync(vaultPath, Password);
        await service.ConfirmRecoveryKeyAsync(
            vaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);
    }

    private static async Task<InProcessHostVaultClient>
        CreateUnlockedClientAsync(string vaultPath)
    {
        var client = new InProcessHostVaultClient();
        await client.UnlockAsync(
            vaultPath,
            Password,
            VaultCredentialType.Password);
        return client;
    }

    private static async Task WaitForWindowCloseAsync(Window window)
    {
        var closed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        await closed.Task.WaitAsync(TimeSpan.FromMinutes(10));
    }

    private static T Find<T>(FrameworkElement element, string name)
        where T : class
    {
        return element.FindName(name) as T
            ?? throw new InvalidOperationException(
                $"Control {name} was not found.");
    }

    private static async Task RunOnStaAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await test();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(20));
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
