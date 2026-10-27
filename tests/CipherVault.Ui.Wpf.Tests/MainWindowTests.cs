using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;
using CipherVault.Host.Client;
using CipherVault.Security.Win;
using CipherVault.Ui.Wpf;

namespace CipherVault.Ui.Wpf.Tests;

public sealed class MainWindowTests
{
    private const string Password = "wpf integration password";

    [Fact]
    public async Task WindowUnlocksLoadsCatalogAndClearsAfterLock()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            var window = fixture.CreateWindow();
            try
            {
                window.Show();
                await WaitUntilAsync(
                    () => Find<TextBox>(window, "VaultPathTextBox").Text ==
                        Path.Combine(fixture.Root, "vault"),
                    TimeSpan.FromSeconds(10));
                Find<PasswordBox>(window, "PasswordBox").Password = Password;
                Find<Button>(window, "UnlockButton").RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "StatusText").Text == "已解锁",
                    TimeSpan.FromSeconds(10));
                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "CatalogSummaryText").Text.Contains(
                        "当前显示 0 篇",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));
                Assert.Empty(Find<PasswordBox>(window, "PasswordBox").Password);

                var workspace = Find<DocumentWorkspaceView>(
                    window,
                    "DocumentWorkspace");
                Find<TextBox>(workspace, "EditorBox").Text = "decrypted content";
                Find<Button>(window, "LockButton").RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "StatusText").Text == "已锁定",
                    TimeSpan.FromSeconds(10));

                Assert.Empty(Find<TextBox>(workspace, "EditorBox").Text);
                Assert.Null(Find<DataGrid>(window, "CatalogGrid").ItemsSource);
                Assert.Equal("资料库已锁定。", Find<TextBlock>(window, "CatalogSummaryText").Text);
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task RefreshLoadsNoteCreatedByAnotherService()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            var window = fixture.CreateWindow();
            try
            {
                window.Show();
                await UnlockWindowAsync(window, fixture.Root);
                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "CatalogSummaryText").Text.Contains(
                        "当前显示 0 篇",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));

                await using (var writer = new VaultService(new AlwaysActiveActivityProvider()))
                {
                    await writer.UnlockAsync(
                        Path.Combine(fixture.Root, "vault"),
                        Password,
                        VaultCredentialType.Password);
                    await writer.CreateNoteAsync(
                        "external write",
                        "inbox",
                        "md",
                        "created outside the desktop session",
                        null,
                        VaultActorKind.Mcp);
                }

                InvokePrivate(
                    window,
                    "RefreshCatalogButton_Click",
                    window,
                    new RoutedEventArgs());
                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "CatalogSummaryText").Text.Contains(
                        "当前显示 1 篇",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));
                Assert.Single(Find<DataGrid>(window, "CatalogGrid").Items.Cast<object>());
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ReopenedWindowLoadsPersistedNote()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            await using (var writer = new VaultService(new AlwaysActiveActivityProvider()))
            {
                await writer.UnlockAsync(
                    Path.Combine(fixture.Root, "vault"),
                    Password,
                    VaultCredentialType.Password);
                await writer.CreateNoteAsync(
                    "persisted note",
                    "inbox",
                    "md",
                    "survives a new desktop window",
                    null,
                    VaultActorKind.Mcp);
            }

            var firstWindow = fixture.CreateWindow();
            try
            {
                firstWindow.Show();
                await UnlockWindowAsync(firstWindow, fixture.Root);
                await WaitUntilAsync(
                    () => Find<TextBlock>(firstWindow, "CatalogSummaryText").Text.Contains(
                        "当前显示 1 篇",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));
            }
            finally
            {
                await fixture.DisposeWindowAsync(firstWindow);
                firstWindow.Close();
            }

            var secondWindow = fixture.CreateWindow();
            try
            {
                secondWindow.Show();
                await UnlockWindowAsync(secondWindow, fixture.Root);
                await WaitUntilAsync(
                    () => Find<TextBlock>(secondWindow, "CatalogSummaryText").Text.Contains(
                        "当前显示 1 篇",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));
                Assert.Single(Find<DataGrid>(secondWindow, "CatalogGrid").Items.Cast<object>());
            }
            finally
            {
                await fixture.DisposeWindowAsync(secondWindow);
                secondWindow.Close();
            }
        });
    }

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
    public Task HostModeHidesLocalCredentialInputs()
    {
        return RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var window = new MainWindow(
                fixture.VaultPath,
                new InProcessHostVaultClient(promptsForCredentials: true));
            try
            {
                Assert.Equal(
                    Visibility.Collapsed,
                    Find<ComboBox>(window, "CredentialTypeComboBox").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Find<PasswordBox>(window, "PasswordBox").Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    Find<TextBlock>(window, "HostCredentialHintText").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Find<PasswordBox>(window, "MaintenancePasswordBox").Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    Find<TextBlock>(window, "HostRestoreCredentialHintText").Visibility);
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
    public async Task MissingVaultPathIsShownAsUninitialized()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var window = fixture.CreateWindow();
            try
            {
                window.Show();

                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "StatusText").Text == "未初始化",
                    TimeSpan.FromSeconds(10));
                Assert.Contains(
                    "请输入密码并点击“初始化”",
                    Find<TextBlock>(window, "SessionDetailText").Text,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "资料库尚未初始化。",
                    Find<TextBlock>(window, "CatalogSummaryText").Text);
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ExistingUninitializedDirectoryRequiresNewPath()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            Directory.CreateDirectory(Path.Combine(fixture.Root, "vault"));
            var window = fixture.CreateWindow();
            try
            {
                window.Show();

                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "StatusText").Text == "未初始化",
                    TimeSpan.FromSeconds(10));
                var detail = Find<TextBlock>(window, "SessionDetailText").Text;
                Assert.Contains("目录已存在", detail, StringComparison.Ordinal);
                Assert.Contains("初始化需要新目录", detail, StringComparison.Ordinal);
                Assert.Equal(
                    "资料库尚未初始化。",
                    Find<TextBlock>(window, "CatalogSummaryText").Text);
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task IncompleteVaultFilesAreShownAsVaultError()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var vaultPath = Path.Combine(fixture.Root, "vault");
            Directory.CreateDirectory(vaultPath);
            File.WriteAllText(Path.Combine(vaultPath, "vault.keys"), "{}");
            var window = fixture.CreateWindow();
            try
            {
                window.Show();

                await WaitUntilAsync(
                    () => Find<TextBlock>(window, "StatusText").Text == "资料库异常",
                    TimeSpan.FromSeconds(10));
                var detail = Find<TextBlock>(window, "SessionDetailText").Text;
                Assert.Contains("vault.keys", detail, StringComparison.Ordinal);
                Assert.Contains("vault.db", detail, StringComparison.Ordinal);
                Assert.Equal(
                    "资料库不可用。",
                    Find<TextBlock>(window, "CatalogSummaryText").Text);
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
                window.Close();
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
                var workspace = Find<DocumentWorkspaceView>(
                    window,
                    "DocumentWorkspace");
                Find<TextBox>(workspace, "EditorBox").Text = "decrypted document";
                Find<TextBox>(window, "MaintenanceOutput").Text = "decrypted output";
                Find<TextBlock>(window, "CatalogSummaryText").Text = "catalog";
                Find<TextBlock>(window, "TrashSummaryText").Text = "trash";
                Find<Button>(window, "LoadMoreCatalogButton").IsEnabled = true;
                Find<Button>(window, "LoadMoreTrashButton").IsEnabled = true;
                SetField(window, "_importJobId", (Guid?)Guid.NewGuid());

                InvokePrivate(window, "ClearSensitiveUi", "资料库已锁定。");

                Assert.Empty(Find<TextBox>(workspace, "EditorBox").Text);
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
    public Task MarkdownPreviewEnablesDocumentInteractions()
    {
        return RunOnStaAsync(() =>
        {
            var workspace = new DocumentWorkspaceView();
            var preview = Find<RichTextBox>(workspace, "PreviewBox");
            Assert.True(preview.IsDocumentEnabled);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task DocumentWorkspaceExposesLinkAuthoringControls()
    {
        return RunOnStaAsync(() =>
        {
            var workspace = new DocumentWorkspaceView();
            Assert.NotNull(Find<TextBox>(workspace, "TitleEditorBox"));
            Assert.NotNull(Find<CheckBox>(workspace, "SyncBacklinksCheckBox"));
            Assert.NotNull(Find<Popup>(workspace, "LinkSuggestionPopup"));
            Assert.NotNull(Find<ListBox>(workspace, "LinkSuggestionList"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DocumentWorkspaceAutocompletesWikiLinks()
    {
        await RunOnStaAsync(async () =>
        {
            await using var service = new InProcessHostVaultClient();
            var workspace = new DocumentWorkspaceView();
            workspace.Initialize(service);
            var window = new Window
            {
                Width = 1000,
                Height = 700,
                Content = workspace
            };
            try
            {
                window.Show();
                SetField(
                    workspace,
                    "_linkTargets",
                    new List<VaultCatalogItem>
                    {
                        new(
                            Guid.NewGuid(),
                            "目标文档",
                            "目标文档.md",
                            "md",
                            1,
                            [],
                            [],
                            DateTimeOffset.Parse("2026-10-25T00:00:00Z"))
                    });
                InvokePrivate(workspace, "SetEditMode", true);
                var editor = Find<TextBox>(workspace, "EditorBox");
                editor.Text = "参见 [[目";
                editor.CaretIndex = editor.Text.Length;
                InvokePrivate(workspace, "UpdateLinkSuggestions");
                var popup = Find<Popup>(workspace, "LinkSuggestionPopup");
                var list = Find<ListBox>(workspace, "LinkSuggestionList");
                Assert.True(popup.IsOpen);
                var suggestion = Assert.IsType<LinkSuggestion>(
                    Assert.Single(list.Items));
                InvokePrivate(workspace, "AcceptLinkSuggestion", suggestion);

                Assert.Equal("参见 [[目标文档]]", editor.Text);
                Assert.False(popup.IsOpen);
            }
            finally
            {
                workspace.Clear();
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DocumentWorkspaceLoadsLinkTargetCatalog()
    {
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            await using var service = new InProcessHostVaultClient();
            await service.UnlockAsync(
                fixture.VaultPath,
                Password,
                VaultCredentialType.Password);
            await service.CreateNoteAsync("候选一", null, "md", "one", null);
            await service.CreateNoteAsync("候选二", null, "md", "two", null);
            var workspace = new DocumentWorkspaceView();
            workspace.Initialize(service);

            await (Task)(InvokePrivate(
                workspace,
                "EnsureLinkTargetsAsync")
                ?? throw new InvalidOperationException(
                    "EnsureLinkTargetsAsync returned null."));
            var targets = Assert.IsAssignableFrom<List<VaultCatalogItem>>(
                GetField(workspace, "_linkTargets"));

            Assert.Equal(2, targets.Count);
            workspace.Clear();
        });
    }

    [Fact]
    public async Task DocumentWorkspaceRenamesAndSynchronizesBacklinks()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            var window = fixture.CreateWindow();
            try
            {
                window.Show();
                await UnlockWindowAsync(window, fixture.Root);
                var service = Assert.IsAssignableFrom<IHostVaultClient>(
                    GetField(window, "_service"));
                var target = await service.CreateNoteAsync(
                    "旧目标",
                    null,
                    "md",
                    "# 旧目标",
                    null);
                var source = await service.CreateNoteAsync(
                    "来源",
                    null,
                    "md",
                    "[[旧目标]]",
                    null);
                var workspace = Find<DocumentWorkspaceView>(
                    window,
                    "DocumentWorkspace");
                Assert.True(await workspace.LoadDocumentAsync(target.DocumentId));
                Find<Button>(workspace, "EditButton").RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                var titleEditor = Find<TextBox>(workspace, "TitleEditorBox");
                titleEditor.Text = "新目标";
                Assert.True(Find<CheckBox>(workspace, "SyncBacklinksCheckBox").IsChecked);
                Find<Button>(workspace, "SaveButton").RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                await WaitUntilAsync(
                    async () =>
                    {
                        var read = await service.ReadNoteAsync(
                            source.DocumentId,
                            0,
                            16_000);
                        return read.Revision == 2 &&
                            read.Content == "[[新目标]]";
                    },
                    TimeSpan.FromSeconds(10));
                await WaitUntilAsync(
                    () => Find<DataGrid>(window, "CatalogGrid")
                        .Items
                        .Cast<VaultCatalogItem>()
                        .Any(item => item.Title == "新目标"),
                    TimeSpan.FromSeconds(10));
                Assert.Equal(
                    "新目标",
                    Find<TextBlock>(workspace, "DocumentTitle").Text);
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DocumentWorkspaceLoadsBacklinksAndSavesEdits()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            var window = fixture.CreateWindow();
            try
            {
                window.Show();
                await UnlockWindowAsync(window, fixture.Root);
                var service = Assert.IsAssignableFrom<IHostVaultClient>(
                    GetField(window, "_service"));
                var target = await service.CreateNoteAsync(
                    "目标",
                    null,
                    "md",
                    "# 目标\n正文",
                    null);
                await service.CreateNoteAsync(
                    "来源",
                    null,
                    "md",
                    "[[目标]]",
                    null);

                var workspace = Find<DocumentWorkspaceView>(
                    window,
                    "DocumentWorkspace");
                Assert.True(await workspace.LoadDocumentAsync(target.DocumentId));
                Assert.Equal(
                    "目标",
                    Find<TextBlock>(workspace, "DocumentTitle").Text);
                Assert.Single(Find<ListBox>(workspace, "BacklinkList").Items);
                Find<TabControl>(workspace, "ContextTabs").SelectedIndex = 2;
                InvokePrivate(
                    workspace,
                    "RefreshGraphButton_Click",
                    workspace,
                    new RoutedEventArgs());
                await WaitUntilAsync(
                    () => Find<TextBlock>(workspace, "GraphSummary").Text.Contains(
                        "2 个节点",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));

                Find<Button>(workspace, "EditButton").RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                var editor = Find<TextBox>(workspace, "EditorBox");
                editor.Text += "\n修改内容";
                Assert.True(workspace.HasUnsavedChanges);
                Find<Button>(workspace, "SaveButton").RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                await WaitUntilAsync(
                    async () =>
                    {
                        var read = await service.ReadNoteAsync(
                            target.DocumentId,
                            0,
                            16_000);
                        return read.Revision == 2 &&
                            read.Content.Contains("修改内容", StringComparison.Ordinal);
                    },
                    TimeSpan.FromSeconds(10));
            }
            finally
            {
                await fixture.DisposeWindowAsync(window);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DocumentWorkspaceRendersLocalGraph()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            await using var service = new InProcessHostVaultClient();
            await service.UnlockAsync(
                fixture.VaultPath,
                Password,
                VaultCredentialType.Password);
            var target = await service.CreateNoteAsync(
                "图目标",
                null,
                "md",
                "# 图目标\n正文",
                null);
            await service.CreateNoteAsync(
                "图来源",
                null,
                "md",
                "[[图目标]]",
                null);
            var workspace = new DocumentWorkspaceView();
            workspace.Initialize(service);
            var window = new Window
            {
                Width = 1000,
                Height = 700,
                Content = workspace
            };
            try
            {
                window.Show();
                Assert.True(await workspace.LoadDocumentAsync(target.DocumentId));
                Find<TabControl>(workspace, "ContextTabs").SelectedIndex = 2;
                InvokePrivate(
                    workspace,
                    "RefreshGraphButton_Click",
                    workspace,
                    new RoutedEventArgs());
                await WaitUntilAsync(
                    () => Find<TextBlock>(workspace, "GraphSummary").Text.Contains(
                        "2 个节点",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));
                var graphHost = Find<DockPanel>(workspace, "GraphHost");
                var graphCanvas = await WaitUntilGraphCanvasAsync(
                    graphHost,
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(graphCanvas.RenderTransform);
                AssertGraphContainsVisiblePixels(graphHost);
            }
            finally
            {
                workspace.Clear();
                window.Close();
            }
        });
    }

    [Fact]
    public async Task GraphRefreshDoesNotRenderAfterWorkspaceIsCleared()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }

        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            await fixture.InitializeVaultAsync();
            await using var service = new InProcessHostVaultClient();
            await service.UnlockAsync(
                fixture.VaultPath,
                Password,
                VaultCredentialType.Password);
            var target = await service.CreateNoteAsync(
                "待清空图",
                null,
                "md",
                "# 待清空图",
                null);
            var workspace = new DocumentWorkspaceView();
            workspace.Initialize(service);
            var window = new Window
            {
                Width = 1000,
                Height = 700,
                Content = workspace
            };
            try
            {
                window.Show();
                Assert.True(await workspace.LoadDocumentAsync(target.DocumentId));
                Find<TabControl>(workspace, "ContextTabs").SelectedIndex = 2;
                InvokePrivate(
                    workspace,
                    "RefreshGraphButton_Click",
                    workspace,
                    new RoutedEventArgs());
                workspace.Clear();
                await Task.Delay(500);

                var graphHost = Find<DockPanel>(workspace, "GraphHost");
                Assert.Empty(graphHost.Children);
                Assert.Equal(
                    "点击“刷新图谱”加载当前文档关系",
                    Find<TextBlock>(workspace, "GraphSummary").Text);
            }
            finally
            {
                workspace.Clear();
                window.Close();
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

    [Fact]
    public Task NormalCloseDoesNotRequestGlobalLock()
    {
        return RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var client = new InProcessHostVaultClient();
            var window = new MainWindow(fixture.VaultPath, client);
            window.Show();

            window.Close();
            await WaitUntilAsync(
                () => !window.IsVisible,
                TimeSpan.FromSeconds(5));

            Assert.Equal(0, client.LockCallCount);
        });
    }

    [Fact]
    public Task ExitAndLockLocksBeforeClosing()
    {
        return RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var client = new InProcessHostVaultClient();
            var window = new MainWindow(fixture.VaultPath, client);
            window.Show();

            InvokePrivate(
                window,
                "ExitAndLockButton_Click",
                window,
                new RoutedEventArgs());
            await WaitUntilAsync(
                () => !window.IsVisible,
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, client.LockCallCount);
            Assert.Equal("desktop-exit", client.LastLockReason);
        });
    }

    [Fact]
    public async Task StopHostClosesDesktopAfterStoppingService()
    {
        if (!ShouldRunInteractiveUiTests())
        {
            return;
        }
        using var uiLock = UiAutomationLock.Acquire();
        await RunOnStaAsync(async () =>
        {
            using var fixture = new WindowFixture();
            var client = new InProcessHostVaultClient();
            var window = new MainWindow(fixture.VaultPath, client);
            window.Show();

            await (Task)(InvokePrivate(
                window,
                "StopHostAsync")
                ?? throw new InvalidOperationException("StopHostAsync returned null."));
            await WaitUntilAsync(
                () => !window.IsVisible,
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, client.StopHostCallCount);
        });
    }

    private static T Find<T>(FrameworkElement element, string name)
        where T : class
    {
        return element.FindName(name) as T
            ?? throw new InvalidOperationException($"Control {name} was not found.");
    }

    private static async Task UnlockWindowAsync(MainWindow window, string fixtureRoot)
    {
        await WaitUntilAsync(
            () => Find<TextBox>(window, "VaultPathTextBox").Text ==
                Path.Combine(fixtureRoot, "vault"),
            TimeSpan.FromSeconds(10));
        Find<PasswordBox>(window, "PasswordBox").Password = Password;
        Find<Button>(window, "UnlockButton").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(
            () => Find<TextBlock>(window, "StatusText").Text == "已解锁",
            TimeSpan.FromSeconds(10));
    }

    private static object? InvokePrivate(
        object target,
        string methodName,
        params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {methodName} was not found.");
        return method.Invoke(target, arguments);
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
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await test();
                    completion.SetResult(true);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("WPF condition was not met before timeout.");
            }
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "WPF asynchronous condition was not met before timeout.");
            }
            await Task.Delay(25);
        }
    }

    private static async Task<Canvas> WaitUntilGraphCanvasAsync(
        DockPanel graphHost,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            graphHost.UpdateLayout();
            var canvases = graphHost.Children.OfType<Canvas>().ToArray();
            if (graphHost.ActualWidth > 100 &&
                graphHost.ActualHeight > 100 &&
                canvases.Length == 1 &&
                canvases[0].ActualWidth > 0 &&
                canvases[0].ActualHeight > 0 &&
                canvases[0].Children.Count > 0)
            {
                return canvases[0];
            }
            await Task.Delay(25);
        }
        var children = string.Join(
            ", ",
            graphHost.Children
                .Cast<FrameworkElement>()
                .Select(child =>
                    $"{child.GetType().Name}=" +
                    $"{child.ActualWidth:0.#}x{child.ActualHeight:0.#}"));
        throw new TimeoutException(
            $"Graph host={graphHost.ActualWidth:0.#}x" +
            $"{graphHost.ActualHeight:0.#}; children=[{children}]");
    }

    private static void AssertGraphContainsVisiblePixels(FrameworkElement graphHost)
    {
        var width = Math.Max(1, (int)Math.Ceiling(graphHost.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(graphHost.ActualHeight));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(graphHost);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var colors = new HashSet<int>();
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var color = BitConverter.ToInt32(pixels, offset);
            if (colors.Add(color) && colors.Count > 1)
            {
                return;
            }
        }
        Assert.Fail("The local graph rendered no visible content.");
    }

    private static bool ShouldRunInteractiveUiTests()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return false;
        }
        if (new WindowsSystemActivityProvider().GetSnapshot().IsWorkstationLocked)
        {
            return false;
        }
        if (string.Equals(
                Environment.GetEnvironmentVariable("CIPHER_VAULT_RUN_UI_TESTS"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return !string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
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
            VaultPath = Path.Combine(
                Root,
                useRelativeVaultPath ? "relative-vault" : "vault");
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(new
                {
                    configVersion = 2,
                    vaultPath = useRelativeVaultPath
                        ? "relative-vault"
                        : VaultPath,
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
        public string VaultPath { get; }

        public MainWindow CreateWindow()
        {
            return new MainWindow(VaultPath, new InProcessHostVaultClient());
        }

        public async Task InitializeVaultAsync()
        {
            await using var service = new VaultService(new AlwaysActiveActivityProvider());
            var initialized = await service.InitializeAsync(
                Path.Combine(Root, "vault"),
                Password);
            await service.ConfirmRecoveryKeyAsync(
                Path.Combine(Root, "vault"),
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
        }

        public async Task DisposeWindowAsync(MainWindow window)
        {
            var field = typeof(MainWindow).GetField(
                "_service",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Vault service field was not found.");
            var service = Assert.IsAssignableFrom<IHostVaultClient>(
                field.GetValue(window));
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

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }

    private static class UiAutomationLock
    {
        private const string Name = @"Local\CipherVault.WindowsUiAutomationTests";

        public static IDisposable Acquire()
        {
            var semaphore = new Semaphore(1, 1, Name);
            if (!semaphore.WaitOne(TimeSpan.FromSeconds(30)))
            {
                semaphore.Dispose();
                throw new TimeoutException("Timed out waiting for Windows UI automation lock.");
            }
            return new Releaser(semaphore);
        }

        private sealed class Releaser : IDisposable
        {
            private Semaphore? _semaphore;

            public Releaser(Semaphore semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                var semaphore = Interlocked.Exchange(ref _semaphore, null);
                if (semaphore is null)
                {
                    return;
                }
                semaphore.Release();
                semaphore.Dispose();
            }
        }
    }
}
