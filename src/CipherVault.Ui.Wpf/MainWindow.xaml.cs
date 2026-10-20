using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;

namespace CipherVault.Ui.Wpf;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly VaultService _service;
    private readonly DispatcherTimer _statusTimer;
    private readonly string _configuredVaultPath;
    private readonly bool _startupFailed;
    private Guid? _importJobId;
    private string? _catalogCursor;
    private string? _searchCursor;
    private string? _trashCursor;
    private string? _activeSearchQuery;
    private bool _searchMode;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        ResolvedCipherVaultConfig configuration;
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            configuration = CipherVaultConfiguration.Load(
                CreateCommandLineOverrides(arguments),
                GetOption(arguments, "--config"));
        }
        catch (VaultException exception)
        {
            MessageBox.Show(
                $"错误 {exception.Code}：{exception.Message}",
                "CipherVault 加密资料库",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _startupFailed = true;
            configuration = new ResolvedCipherVaultConfig(
                CipherVaultConfiguration.DefaultVaultPath,
                VaultSessionOptions.Default,
                McpAccessMode.ReadWrite,
                CipherVaultConfiguration.DefaultConfigPath);
        }
        _configuredVaultPath = configuration.VaultPath
            ?? CipherVaultConfiguration.DefaultVaultPath;
        _service = new VaultService(
            new WindowsSystemActivityProvider(),
            sessionOptions: configuration.SessionOptions);
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupFailed)
        {
            Application.Current.Shutdown(2);
            return;
        }
        VaultPathTextBox.Text = _configuredVaultPath;
        _statusTimer.Start();
        await RefreshStatusAsync();
        Activate();
        PasswordBox.Focus();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        await _service.DisposeAsync();
    }

    private async void InitializeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var path = VaultPathTextBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(password))
        {
            ShowInfo("请输入资料库文件夹和密码。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await _service.InitializeAsync(path, password);
            PasswordBox.Clear();
            RecoveryKeyTextBox.Text = result.RecoveryKey;
            RecoveryConfirmTextBox.Clear();
            RecoveryPanel.Visibility = Visibility.Visible;
            await RefreshStatusAsync();
        });
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var path = VaultPathTextBox.Text.Trim();
        var credentialType = GetCredentialType(CredentialTypeComboBox);
        var credential = ReadCredential(
            credentialType,
            PasswordBox,
            RecoveryKeyUnlockBox);
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(credential))
        {
            ShowInfo("请输入资料库文件夹和凭据。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.UnlockAsync(path, credential, credentialType);
            PasswordBox.Clear();
            RecoveryKeyUnlockBox.Clear();
            await RefreshStatusAsync();
            await RefreshCatalogAsync();
            await RefreshTrashAsync();
        });
    }

    private void CredentialTypeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PasswordBox is null || RecoveryKeyUnlockBox is null)
        {
            return;
        }
        var useRecoveryKey = GetCredentialType(CredentialTypeComboBox) ==
            VaultCredentialType.RecoveryKey;
        PasswordBox.Visibility = useRecoveryKey ? Visibility.Collapsed : Visibility.Visible;
        RecoveryKeyUnlockBox.Visibility =
            useRecoveryKey ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Clear();
        RecoveryKeyUnlockBox.Clear();
        if (useRecoveryKey)
        {
            RecoveryKeyUnlockBox.Focus();
        }
        else
        {
            PasswordBox.Focus();
        }
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.LockAsync();
            await RefreshStatusAsync();
            ClearSensitiveUi();
        });
    }

    private void CopyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(RecoveryKeyTextBox.Text))
        {
            Clipboard.SetText(RecoveryKeyTextBox.Text);
        }
    }

    private async void ConfirmRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var recoveryKey = NormalizeRecoveryKey(RecoveryConfirmTextBox.Text);
        if (string.IsNullOrEmpty(recoveryKey))
        {
            ShowInfo("请重新输入恢复密钥，确认已保存。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.ConfirmRecoveryKeyAsync(
                VaultPathTextBox.Text.Trim(),
                recoveryKey,
                VaultActorKind.UserUi);
            RecoveryPanel.Visibility = Visibility.Collapsed;
            RecoveryKeyTextBox.Clear();
            RecoveryConfirmTextBox.Clear();
            await RefreshStatusAsync();
        });
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _busy || e.Source != MainTabs)
        {
            return;
        }
        if (MainTabs.SelectedIndex == 1)
        {
            await RefreshCatalogAsync();
        }
        else if (MainTabs.SelectedIndex == 2)
        {
            await RefreshTrashAsync();
        }
    }

    private async void RefreshCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCatalogAsync();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var query = SearchTextBox.Text.Trim();
        if (query.Length == 0)
        {
            await RefreshCatalogAsync();
            return;
        }
        await RunBusyAsync(async () =>
        {
            var page = await _service.SearchNotesAsync(query, null, null, 20);
            _searchMode = true;
            _activeSearchQuery = query;
            _catalogCursor = null;
            _searchCursor = page.NextCursor;
            SetCatalogItems(
                page.Hits.Cast<object>().ToList(),
                $"匹配 {page.Coverage.MatchedDocuments} 篇，扫描 {page.Coverage.ScannedDocuments} 篇",
                page.NextCursor);
        });
    }

    private async void LoadMoreCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        if (_searchMode)
        {
            if (_searchCursor is null || _activeSearchQuery is null)
            {
                return;
            }
            await RunBusyAsync(async () =>
            {
                var page = await _service.SearchNotesAsync(
                    _activeSearchQuery,
                    null,
                    _searchCursor,
                    20);
                _searchCursor = page.NextCursor;
                AppendCatalogItems(
                    page.Hits.Cast<object>().ToList(),
                    $"匹配 {page.Coverage.MatchedDocuments} 篇，扫描 {page.Coverage.ScannedDocuments} 篇",
                    page.NextCursor);
            });
            return;
        }
        if (_catalogCursor is null)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var page = await _service.ListCatalogAsync(null, _catalogCursor, 100);
            _catalogCursor = page.NextCursor;
            AppendCatalogItems(
                page.Items.Cast<object>().ToList(),
                null,
                page.NextCursor);
        });
    }

    private async void CatalogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || CatalogGrid.SelectedItem is null)
        {
            return;
        }
        var id = CatalogGrid.SelectedItem switch
        {
            VaultCatalogItem item => item.DocumentId,
            VaultSearchHit hit => hit.DocumentId,
            _ => Guid.Empty
        };
        if (id == Guid.Empty)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var document = await _service.ReadNoteAsync(id, 0, 16_000);
            DocumentViewer.Text =
                $"{document.Title}\n{document.VirtualPath}\n版本 {document.Revision}\n\n{document.Content}" +
                (document.Done ? string.Empty : "\n\n[... 内容未完全显示，请继续读取 ...]");
        });
    }

    private async void RefreshTrashButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshTrashAsync();
    }

    private async void LoadMoreTrashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _trashCursor is null)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var page = await _service.ListTrashAsync(_trashCursor, 100);
            _trashCursor = page.NextCursor;
            AppendTrashItems(page.Items);
        });
    }

    private async void RestoreTrashButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrashGrid.SelectedItem is not VaultTrashItem item)
        {
            ShowInfo("请选择要恢复的文档。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.RestoreTrashAsync(
                item.DocumentId,
                item.Revision,
                null,
                VaultActorKind.UserUi);
            await RefreshTrashAsync();
            await RefreshCatalogAsync();
        });
    }

    private async void PurgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrashGrid.SelectedItem is not VaultTrashItem item)
        {
            ShowInfo("请选择要永久删除的文档。");
            return;
        }
        if (MessageBox.Show(
                this,
                $"永久删除“{item.Title}”？此操作无法从资料库中撤销。",
                "永久删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.PurgeNoteAsync(item.DocumentId, VaultActorKind.UserUi);
            await RefreshTrashAsync();
        });
    }

    private void BrowseImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要导入的文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            ImportSourceTextBox.Text = dialog.FolderName;
        }
    }

    private void BrowseExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择导出父文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            ExportDestinationTextBox.Text = Path.Combine(
                dialog.FolderName,
                $"资料库导出-{DateTime.Now:yyyyMMdd-HHmmss}");
        }
    }

    private void BrowseRestoreTargetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择恢复父文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            RestoreTargetTextBox.Text = Path.Combine(
                dialog.FolderName,
                $"资料库恢复-{DateTime.Now:yyyyMMdd-HHmmss}");
        }
    }

    private async void ImportPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var preview = await _service.PreviewImportAsync(
                ImportSourceTextBox.Text.Trim(),
                VaultActorKind.UserUi);
            _importJobId = preview.JobId;
            PrintJson(preview);
        });
    }

    private async void ImportApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        if (_importJobId is null)
        {
            ShowInfo("请先预览导入内容。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await _service.CommitImportAsync(
                _importJobId.Value,
                VaultActorKind.UserUi);
            PrintJson(result);
            _importJobId = null;
            await RefreshCatalogAsync();
        });
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await _service.ExportSnapshotAsync(
                ExportDestinationTextBox.Text.Trim(),
                VaultActorKind.UserUi);
            PrintJson(result);
        });
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "CipherVault 备份 (*.cvbackup)|*.cvbackup",
            DefaultExt = ".cvbackup",
            AddExtension = true,
            FileName = $"CipherVault-{DateTime.Now:yyyyMMdd}.cvbackup"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await _service.CreateBackupAsync(
                dialog.FileName,
                VaultActorKind.UserUi);
            RestoreBackupTextBox.Text = result.BackupPath;
            PrintJson(result);
        });
    }

    private void OpenBackupFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CipherVault 备份 (*.cvbackup)|*.cvbackup|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            RestoreBackupTextBox.Text = dialog.FileName;
        }
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var credentialType = GetCredentialType(RestoreCredentialTypeComboBox);
        var credential = ReadCredential(
            credentialType,
            RestorePasswordBox,
            RestoreRecoveryKeyBox);
        if (string.IsNullOrEmpty(credential))
        {
            ShowInfo("请输入备份密码或离线恢复密钥。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await _service.RestoreBackupAsync(
                RestoreBackupTextBox.Text.Trim(),
                RestoreTargetTextBox.Text.Trim(),
                credential,
                credentialType,
                VaultActorKind.UserUi);
            PrintJson(result);
            RestorePasswordBox.Clear();
            RestoreRecoveryKeyBox.Clear();
        });
    }

    private void RestoreCredentialTypeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (RestorePasswordBox is null || RestoreRecoveryKeyBox is null)
        {
            return;
        }
        var useRecoveryKey = GetCredentialType(RestoreCredentialTypeComboBox) ==
            VaultCredentialType.RecoveryKey;
        RestorePasswordBox.Visibility =
            useRecoveryKey ? Visibility.Collapsed : Visibility.Visible;
        RestoreRecoveryKeyBox.Visibility =
            useRecoveryKey ? Visibility.Visible : Visibility.Collapsed;
        RestorePasswordBox.Clear();
        RestoreRecoveryKeyBox.Clear();
        if (useRecoveryKey)
        {
            RestoreRecoveryKeyBox.Focus();
        }
        else
        {
            RestorePasswordBox.Focus();
        }
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        await RunBusyAsync(async () => PrintJson(await _service.VerifyVaultAsync()));
    }

    private async void RebuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var count = await _service.RebuildSearchIndexAsync(VaultActorKind.UserUi);
            MaintenanceOutput.Text = $"搜索索引行数：{count}";
        });
    }

    private async void AuditButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        await RunBusyAsync(async () => PrintJson(await _service.GetAuditPageAsync(null, 100)));
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var current = MaintenancePasswordBox.Password;
        var next = NewPasswordBox.Password;
        var confirmation = ConfirmNewPasswordBox.Password;
        if (string.IsNullOrEmpty(current) || !string.Equals(next, confirmation, StringComparison.Ordinal))
        {
            ShowInfo("请输入当前密码，并确认两次新密码一致。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.ChangePasswordAsync(
                VaultPathTextBox.Text.Trim(),
                current,
                next,
                VaultActorKind.UserUi);
            MaintenancePasswordBox.Clear();
            NewPasswordBox.Clear();
            ConfirmNewPasswordBox.Clear();
            MaintenanceOutput.Text = "密码已修改。";
        });
    }

    private async void RotateRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        var current = MaintenancePasswordBox.Password;
        if (string.IsNullOrEmpty(current))
        {
            ShowInfo("请输入当前密码。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var recoveryKey = await _service.RotateRecoveryKeyAsync(
                VaultPathTextBox.Text.Trim(),
                current,
                VaultActorKind.UserUi);
            MaintenancePasswordBox.Clear();
            RecoveryKeyTextBox.Text = recoveryKey;
            RecoveryConfirmTextBox.Clear();
            RecoveryPanel.Visibility = Visibility.Visible;
            MaintenanceOutput.Text = "恢复密钥已轮换。请保存并确认新密钥。";
        });
    }

    private async void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (!_busy)
        {
            await RefreshStatusAsync();
        }
    }

    private async Task<bool> EnsureUnlockedAsync()
    {
        var status = await _service.GetStatusAsync();
        if (status.State is VaultSessionState.Unlocked or VaultSessionState.Expiring)
        {
            if (!status.RecoveryVerified)
            {
                ShowInfo(
                    "请先确认恢复密钥。如果尚未保存，请使用当前密码重新轮换恢复密钥。");
                return false;
            }
            return true;
        }
        ShowInfo("请先解锁资料库。");
        MainTabs.SelectedIndex = 0;
        return false;
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _service.GetStatusAsync();
            if (status.State == VaultSessionState.Locked)
            {
                switch (GetVaultPathState(VaultPathTextBox.Text.Trim()))
                {
                    case VaultPathState.Missing:
                        StatusText.Text = "未初始化";
                        SessionDetailText.Text =
                            "资料库尚未初始化。请输入密码并点击“初始化”。";
                        ClearSensitiveUi("资料库尚未初始化。");
                        return;
                    case VaultPathState.Uninitialized:
                        StatusText.Text = "未初始化";
                        SessionDetailText.Text =
                            "该目录已存在，但未检测到 CipherVault 文件。初始化需要新目录；请换一个目录，或确认无需保留后移除该目录。";
                        ClearSensitiveUi("资料库尚未初始化。");
                        return;
                    case VaultPathState.Incomplete:
                        StatusText.Text = "资料库异常";
                        SessionDetailText.Text =
                            "资料库文件不完整：需要同时存在 vault.keys 和 vault.db。请检查路径，或从备份恢复。";
                        ClearSensitiveUi("资料库不可用。");
                        return;
                }

                StatusText.Text = GetStateText(status.State);
                SessionDetailText.Text = "资料库已锁定。请输入上方密码并点击“解锁”。";
                ClearSensitiveUi();
            }
            else if (!status.RecoveryVerified)
            {
                StatusText.Text = "恢复密钥未确认";
                SessionDetailText.Text =
                    "请先确认恢复密钥，或使用当前密码重新轮换恢复密钥，然后才能访问内容。";
                ClearSensitiveUi();
            }
            else
            {
                StatusText.Text = GetStateText(status.State);
                var idleRemaining = FormatRemaining(status.IdleExpiresAt, DateTimeOffset.UtcNow);
                var hardRemaining = FormatRemaining(status.HardExpiresAt, DateTimeOffset.UtcNow);
                SessionDetailText.Text =
                    $"会话 {status.SessionId} | 空闲锁定倒计时 {idleRemaining} | 最迟锁定倒计时 {hardRemaining}";
            }
        }
        catch (Exception exception)
        {
            SessionDetailText.Text = exception.Message;
        }
    }

    private async Task RefreshCatalogAsync()
    {
        var status = await _service.GetStatusAsync();
        if (status.State == VaultSessionState.Locked || !status.RecoveryVerified)
        {
            ClearSensitiveUi();
            return;
        }
        var page = await _service.ListCatalogAsync(null, null, 100);
        _searchMode = false;
        _activeSearchQuery = null;
        _searchCursor = null;
        _catalogCursor = page.NextCursor;
        SetCatalogItems(
            page.Items.Cast<object>().ToList(),
            null,
            page.NextCursor);
    }

    private async Task RefreshTrashAsync()
    {
        var status = await _service.GetStatusAsync();
        if (status.State == VaultSessionState.Locked || !status.RecoveryVerified)
        {
            ClearSensitiveUi();
            return;
        }
        var page = await _service.ListTrashAsync(null, 100);
        _trashCursor = page.NextCursor;
        SetTrashItems(page.Items.Cast<object>().ToList(), page.NextCursor);
    }

    private void SetCatalogItems(
        IReadOnlyList<object> items,
        string? detail,
        string? nextCursor)
    {
        CatalogGrid.ItemsSource = items;
        UpdateCatalogSummary(items.Count, detail, nextCursor is not null);
    }

    private void AppendCatalogItems(
        IReadOnlyList<object> items,
        string? detail,
        string? nextCursor)
    {
        var combined = CatalogGrid.ItemsSource is IEnumerable<object> existing
            ? existing.ToList()
            : [];
        combined.AddRange(items);
        CatalogGrid.ItemsSource = combined;
        UpdateCatalogSummary(combined.Count, detail, nextCursor is not null);
    }

    private void UpdateCatalogSummary(int count, string? detail, bool hasMore)
    {
        var prefix = detail is null ? string.Empty : $"{detail}；";
        CatalogSummaryText.Text =
            $"{prefix}当前显示 {count} 篇，{(hasMore ? "可继续加载" : "已全部加载")}。";
        LoadMoreCatalogButton.IsEnabled = hasMore;
    }

    private void SetTrashItems(IReadOnlyList<object> items, string? nextCursor)
    {
        TrashGrid.ItemsSource = items;
        UpdateTrashSummary(items.Count, nextCursor is not null);
    }

    private void AppendTrashItems(IEnumerable<object> items)
    {
        var combined = TrashGrid.ItemsSource is IEnumerable<object> existing
            ? existing.ToList()
            : [];
        combined.AddRange(items);
        TrashGrid.ItemsSource = combined;
        UpdateTrashSummary(combined.Count, _trashCursor is not null);
    }

    private void UpdateTrashSummary(int count, bool hasMore)
    {
        TrashSummaryText.Text =
            $"当前显示 {count} 篇，{(hasMore ? "可继续加载" : "已全部加载")}。";
        LoadMoreTrashButton.IsEnabled = hasMore;
    }

    private void ClearSensitiveUi(string summary = "资料库已锁定。")
    {
        _catalogCursor = null;
        _searchCursor = null;
        _trashCursor = null;
        _activeSearchQuery = null;
        _searchMode = false;
        _importJobId = null;
        CatalogGrid.ItemsSource = null;
        TrashGrid.ItemsSource = null;
        DocumentViewer.Clear();
        MaintenanceOutput.Clear();
        CatalogSummaryText.Text = summary;
        TrashSummaryText.Text = summary;
        LoadMoreCatalogButton.IsEnabled = false;
        LoadMoreTrashButton.IsEnabled = false;
    }

    private static VaultPathState GetVaultPathState(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
        {
            return VaultPathState.Missing;
        }

        var fullPath = Path.GetFullPath(vaultPath);
        if (!Directory.Exists(fullPath))
        {
            return VaultPathState.Missing;
        }

        var keyExists = File.Exists(Path.Combine(fullPath, "vault.keys"));
        var databaseExists = File.Exists(Path.Combine(fullPath, "vault.db"));
        if (!keyExists && !databaseExists)
        {
            return VaultPathState.Uninitialized;
        }
        if (!keyExists || !databaseExists)
        {
            return VaultPathState.Incomplete;
        }

        return VaultPathState.Initialized;
    }

    private enum VaultPathState
    {
        Missing,
        Uninitialized,
        Incomplete,
        Initialized
    }

    private static string FormatRemaining(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is null)
        {
            return "未知";
        }
        var remaining = expiresAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "已到期";
        }
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分";
        }
        return $"{remaining.Minutes} 分 {remaining.Seconds} 秒";
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        _busy = true;
        try
        {
            await operation();
        }
        catch (VaultException exception)
        {
            ShowInfo($"错误 {exception.Code}：{exception.Message}");
        }
        catch (Exception exception)
        {
            ShowInfo(exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void PrintJson<T>(T value)
    {
        MaintenanceOutput.Text = JsonSerializer.Serialize(value, JsonOptions);
    }

    private void ShowInfo(string message)
    {
        MessageBox.Show(this, message, "CipherVault 加密资料库", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string NormalizeRecoveryKey(string value)
    {
        return new string(
            value
                .Where(character => !char.IsWhiteSpace(character) && character != '-')
                .Select(char.ToUpperInvariant)
                .ToArray());
    }

    private static VaultCredentialType GetCredentialType(ComboBox selector)
    {
        return selector.SelectedIndex == 1
            ? VaultCredentialType.RecoveryKey
            : VaultCredentialType.Password;
    }

    private static string ReadCredential(
        VaultCredentialType credentialType,
        PasswordBox passwordBox,
        PasswordBox recoveryKeyBox)
    {
        return credentialType == VaultCredentialType.RecoveryKey
            ? NormalizeRecoveryKey(recoveryKeyBox.Password)
            : passwordBox.Password;
    }

    private static string GetStateText(VaultSessionState state)
    {
        return state switch
        {
            VaultSessionState.Locked => "已锁定",
            VaultSessionState.Unlocking => "解锁中",
            VaultSessionState.Unlocked => "已解锁",
            VaultSessionState.Expiring => "即将锁定",
            VaultSessionState.Locking => "锁定中",
            _ => "未知状态"
        };
    }

    private static CipherVaultConfigOverrides CreateCommandLineOverrides(
        string[] arguments)
    {
        return new CipherVaultConfigOverrides
        {
            VaultPath = GetOption(arguments, "--vault"),
            IdleMinutes = ParseOptionalInteger(
                GetOption(arguments, "--idle-minutes"),
                "--idle-minutes"),
            HardHours = ParseOptionalInteger(
                GetOption(arguments, "--hard-hours"),
                "--hard-hours")
        };
    }

    private static int? ParseOptionalInteger(
        string? value,
        string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!int.TryParse(value, out var parsed))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"{option} 必须是整数。");
        }
        return parsed;
    }

    private static string? GetOption(
        string[] arguments,
        string option)
    {
        var optionIndex = Array.FindIndex(
            arguments,
            argument => string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        if (optionIndex < 0)
        {
            return null;
        }
        if (optionIndex + 1 >= arguments.Length ||
            arguments[optionIndex + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"选项 {option} 缺少参数值。");
        }
        return arguments[optionIndex + 1];
    }
}
