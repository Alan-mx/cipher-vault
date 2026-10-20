using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using CipherVault.Auth.Win;
using CipherVault.Core;
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
        VaultSessionOptions sessionOptions;
        try
        {
            sessionOptions = CreateSessionOptions(Environment.GetCommandLineArgs());
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(
                exception.Message,
                "CipherVault 加密资料库",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            sessionOptions = VaultSessionOptions.Default;
        }
        _service = new VaultService(
            new WindowsSystemActivityProvider(),
            sessionOptions: sessionOptions);
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(userProfile, "CipherVault");
        var arguments = Environment.GetCommandLineArgs();
        var vaultOption = Array.FindIndex(
            arguments,
            argument => string.Equals(argument, "--vault", StringComparison.OrdinalIgnoreCase));
        VaultPathTextBox.Text = vaultOption >= 0 && vaultOption + 1 < arguments.Length
            ? arguments[vaultOption + 1]
            : defaultPath;
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
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(password))
        {
            ShowInfo("请输入资料库文件夹和密码。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.UnlockAsync(path, password, VaultCredentialType.Password);
            PasswordBox.Clear();
            await RefreshStatusAsync();
            await RefreshCatalogAsync();
            await RefreshTrashAsync();
        });
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
        var password = MaintenancePasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowInfo("请输入备份密码。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await _service.RestoreBackupAsync(
                RestoreBackupTextBox.Text.Trim(),
                RestoreTargetTextBox.Text.Trim(),
                password,
                VaultCredentialType.Password,
                VaultActorKind.UserUi);
            PrintJson(result);
            MaintenancePasswordBox.Clear();
        });
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
            StatusText.Text = GetStateText(status.State);
            if (status.State == VaultSessionState.Locked)
            {
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

    private void ClearSensitiveUi()
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
        CatalogSummaryText.Text = "资料库已锁定。";
        TrashSummaryText.Text = "资料库已锁定。";
        LoadMoreCatalogButton.IsEnabled = false;
        LoadMoreTrashButton.IsEnabled = false;
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

    private static VaultSessionOptions CreateSessionOptions(string[] arguments)
    {
        var idleMinutes = GetIntegerOption(
            arguments,
            "--idle-minutes",
            "CIPHER_VAULT_IDLE_MINUTES",
            15);
        var hardHours = GetIntegerOption(
            arguments,
            "--hard-hours",
            "CIPHER_VAULT_HARD_HOURS",
            4);
        if (idleMinutes is < 1 or > 1_440)
        {
            throw new ArgumentException("--idle-minutes 必须在 1 到 1440 之间。");
        }
        if (hardHours is < 1 or > 168)
        {
            throw new ArgumentException("--hard-hours 必须在 1 到 168 之间。");
        }
        if (hardHours * 60 < idleMinutes)
        {
            throw new ArgumentException("--hard-hours 不能小于空闲锁定时长。");
        }
        return VaultSessionOptions.FromMinutes(idleMinutes, hardHours);
    }

    private static int GetIntegerOption(
        string[] arguments,
        string option,
        string environmentVariable,
        int defaultValue)
    {
        var optionIndex = Array.FindIndex(
            arguments,
            argument => string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        if (optionIndex < 0)
        {
            var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(environmentValue))
            {
                return defaultValue;
            }
            if (!int.TryParse(environmentValue, out var environmentInteger))
            {
                throw new ArgumentException($"{option} 必须是整数。");
            }
            return environmentInteger;
        }
        if (optionIndex + 1 >= arguments.Length ||
            !int.TryParse(arguments[optionIndex + 1], out var value))
        {
            throw new ArgumentException($"{option} 必须是整数。");
        }
        return value;
    }
}
