using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CipherVault.Core;
using CipherVault.Ui.Wpf;

namespace CipherVault.Ui.Wpf.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public Task CredentialSelectorsTogglePasswordAndRecoveryInputs()
    {
        return RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var window = fixture.CreateWindow();
            try
            {
                var unlockSelector = Find<ComboBox>(window, "CredentialTypeComboBox");
                var passwordBox = Find<PasswordBox>(window, "PasswordBox");
                var recoveryBox = Find<PasswordBox>(window, "RecoveryKeyUnlockBox");
                var restoreSelector = Find<ComboBox>(window, "RestoreCredentialTypeComboBox");
                var restorePassword = Find<PasswordBox>(window, "RestorePasswordBox");
                var restoreRecovery = Find<PasswordBox>(window, "RestoreRecoveryKeyBox");

                unlockSelector.SelectedIndex = 1;
                Assert.Equal(Visibility.Collapsed, passwordBox.Visibility);
                Assert.Equal(Visibility.Visible, recoveryBox.Visibility);

                unlockSelector.SelectedIndex = 0;
                Assert.Equal(Visibility.Visible, passwordBox.Visibility);
                Assert.Equal(Visibility.Collapsed, recoveryBox.Visibility);

                restoreSelector.SelectedIndex = 1;
                Assert.Equal(Visibility.Collapsed, restorePassword.Visibility);
                Assert.Equal(Visibility.Visible, restoreRecovery.Visibility);

                restoreSelector.SelectedIndex = 0;
                Assert.Equal(Visibility.Visible, restorePassword.Visibility);
                Assert.Equal(Visibility.Collapsed, restoreRecovery.Visibility);
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
            }
        });
    }

    [Fact]
    public Task ConfigVaultPathIsInjectedIntoWindow()
    {
        return RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture(useRelativeVaultPath: true);
            var window = fixture.CreateWindow();
            try
            {
                var field = typeof(MainWindow).GetField(
                    "_configuredVaultPath",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Configured Vault path field was not found.");
                var configuredPath = Assert.IsType<string>(field.GetValue(window));
                Assert.Equal(
                    Path.Combine(fixture.Root, "relative-vault"),
                    configuredPath);
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
            }
        });
    }

    [Fact]
    public Task ClearingSensitiveUiRemovesDecryptedState()
    {
        return RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var window = fixture.CreateWindow();
            try
            {
                Find<TextBox>(window, "DocumentViewer").Text = "decrypted document";
                Find<TextBox>(window, "MaintenanceOutput").Text = "decrypted output";
                Find<TextBlock>(window, "CatalogSummaryText").Text = "catalog";
                Find<TextBlock>(window, "TrashSummaryText").Text = "trash";
                Find<Button>(window, "LoadMoreCatalogButton").IsEnabled = true;
                Find<Button>(window, "LoadMoreTrashButton").IsEnabled = true;
                SetField(window, "_importJobId", (Guid?)Guid.NewGuid());

                InvokePrivate(window, "ClearSensitiveUi");

                Assert.Empty(Find<TextBox>(window, "DocumentViewer").Text);
                Assert.Empty(Find<TextBox>(window, "MaintenanceOutput").Text);
                Assert.Equal("资料库已锁定。", Find<TextBlock>(window, "CatalogSummaryText").Text);
                Assert.Equal("资料库已锁定。", Find<TextBlock>(window, "TrashSummaryText").Text);
                Assert.False(Find<Button>(window, "LoadMoreCatalogButton").IsEnabled);
                Assert.False(Find<Button>(window, "LoadMoreTrashButton").IsEnabled);
                Assert.Null(GetField(window, "_importJobId"));
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
            }
        });
    }

    [Fact]
    public Task SessionRemainingTimeIsFormattedForUi()
    {
        return RunOnStaAsync(() =>
        {
            var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
            Assert.Equal(
                "1 小时 5 分",
                InvokePrivateStatic(
                    typeof(MainWindow),
                    "FormatRemaining",
                    now.AddMinutes(65),
                    now));
            Assert.Equal(
                "1 分 30 秒",
                InvokePrivateStatic(
                    typeof(MainWindow),
                    "FormatRemaining",
                    now.AddSeconds(90),
                    now));
            Assert.Equal(
                "已到期",
                InvokePrivateStatic(
                    typeof(MainWindow),
                    "FormatRemaining",
                    now.AddSeconds(-1),
                    now));
            return Task.CompletedTask;
        });
    }

    private static T Find<T>(FrameworkElement element, string name)
        where T : class
    {
        return element.FindName(name) as T
            ?? throw new InvalidOperationException($"Control {name} was not found.");
    }

    private static object? InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {methodName} was not found.");
        return method.Invoke(target, null);
    }

    private static object? InvokePrivateStatic(
        Type type,
        string methodName,
        params object?[] arguments)
    {
        var method = type.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {methodName} was not found.");
        return method.Invoke(null, arguments);
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} was not found.");
        field.SetValue(target, value);
    }

    private static object? GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} was not found.");
        return field.GetValue(target);
    }

    private static async Task RunOnStaAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                test().GetAwaiter().GetResult();
                completion.SetResult(true);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class WindowFixture : IDisposable
    {
        private readonly Dictionary<string, string?> _environment = [];

        public WindowFixture(bool useRelativeVaultPath = false)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-wpf-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            var configPath = Path.Combine(Root, "config.json");
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 1,
                    vaultPath = useRelativeVaultPath
                        ? "relative-vault"
                        : Path.Combine(Root, "vault"),
                    session = new
                    {
                        idleMinutes = 15,
                        hardHours = 4
                    }
                }));
            SetEnvironment("CIPHER_VAULT_CONFIG", configPath);
            foreach (var variable in CipherVaultEnvironmentVariables
                         .Where(variable => variable != "CIPHER_VAULT_CONFIG"))
            {
                SetEnvironment(variable, null);
            }
        }

        public string Root { get; }

        public MainWindow CreateWindow()
        {
            return new MainWindow();
        }

        public async Task DisposeWindowAsync(MainWindow window)
        {
            var field = typeof(MainWindow).GetField(
                "_service",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Vault service field was not found.");
            var service = Assert.IsType<VaultService>(field.GetValue(window));
            await service.DisposeAsync();
        }

        public void Dispose()
        {
            foreach (var item in _environment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private void SetEnvironment(string key, string? value)
        {
            _environment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static readonly string[] CipherVaultEnvironmentVariables =
    [
        "CIPHER_VAULT_CONFIG",
        "CIPHER_VAULT_PATH",
        "CIPHER_VAULT_IDLE_MINUTES",
        "CIPHER_VAULT_HARD_HOURS",
        "CIPHER_VAULT_ACCESS_MODE",
        "CIPHER_VAULT_READ_ONLY"
    ];
}
