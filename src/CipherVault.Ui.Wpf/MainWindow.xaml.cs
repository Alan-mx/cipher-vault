using System.IO;
using System.ComponentModel;
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
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;

namespace CipherVault.Ui.Wpf;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private IHostVaultClient _service = null!;
    private DispatcherTimer _statusTimer = null!;
    private readonly string _configuredVaultPath;
    private readonly bool _startupFailed;
    private Guid? _importJobId;
    private string? _catalogCursor;
    private string? _searchCursor;
    private string? _trashCursor;
    private string? _activeSearchQuery;
    private bool _searchMode;
    private bool _busy;
    private bool _closingConfirmed;
    private bool _suppressCatalogSelection;
    private Guid? _activeDocumentId;
    private int _lastMainTabIndex;
    private bool _suppressMainTabChange;

    public MainWindow()
    {
        InitializeComponent();
        string vaultPath;
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            var paths = CipherVaultServicePaths.Default;
            var manifestPath = GetOption(arguments, "--manifest") ??
                paths.PublicManifestPath;
            var manifest = CipherVaultServiceConfiguration.LoadInstallManifest(
                manifestPath);
            var currentUser = System.Security.Principal.WindowsIdentity
                .GetCurrent().User
                ?? throw new VaultException(
                    VaultErrorCode.StorageError,
                    "无法获取当前 Windows 用户身份。");
            if (!string.Equals(
                    currentUser.Value,
                    manifest.OwnerSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new VaultException(
                    VaultErrorCode.AccessDenied,
                    "当前 Windows 用户不是此 CipherVault 的 Owner。");
            }
            var adminPipe = manifest.Pipes?.Admin;
            var brokerPipe = manifest.Pipes?.Broker;
            if (string.IsNullOrWhiteSpace(adminPipe) ||
                string.IsNullOrWhiteSpace(brokerPipe))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "安装清单中的 pipe 配置无效。");
            }
            using var brokerProcess = StrictServiceBrokerLauncher.EnsureStarted(
                manifest,
                manifestPath);
            var options = StrictServiceClientPolicyFactory.CreateAdminOptions(
                manifest,
                adminPipe,
                currentUser.Value,
                (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId,
                VerifiedProcessKind.AdminUi);
            _service = NamedPipeHostVaultClient.CreateAsync(options)
                .GetAwaiter()
                .GetResult();
            vaultPath = paths.VaultPath;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"错误：{exception.Message}",
                "CipherVault 加密资料库",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _startupFailed = true;
            vaultPath = CipherVaultServicePaths.Default.VaultPath;
        }
        _configuredVaultPath = vaultPath;
        InitializeWindow();
    }

    public MainWindow(string configuredVaultPath, IHostVaultClient service)
    {
        InitializeComponent();
        _configuredVaultPath = Path.GetFullPath(configuredVaultPath);
        _service = service;
        InitializeWindow();
    }

    private void InitializeWindow()
    {
        _lastMainTabIndex = MainTabs.SelectedIndex;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        DocumentWorkspace.Initialize(_service);
        DocumentWorkspace.DocumentSaved += DocumentWorkspace_DocumentSaved;
        ConfigureCredentialMode();
    }

    private async void DocumentWorkspace_DocumentSaved(object? sender, EventArgs e)
    {
        var activeDocumentId = _activeDocumentId;
        await RunBusyAsync(async () =>
        {
            await RefreshCatalogAsync();
            if (activeDocumentId is null)
            {
                return;
            }
            _suppressCatalogSelection = true;
            CatalogGrid.SelectedItem = CatalogGrid.Items
                .Cast<object>()
                .FirstOrDefault(item =>
                    GetDocumentId(item) == activeDocumentId);
            _suppressCatalogSelection = false;
        });
    }

    private void ConfigureCredentialMode()
    {
        if (!_service.PromptsForCredentials)
        {
            return;
        }

        CredentialTypeComboBox.Visibility = Visibility.Collapsed;
        PasswordBox.Visibility = Visibility.Collapsed;
        RecoveryKeyUnlockBox.Visibility = Visibility.Collapsed;
        HostCredentialHintText.Visibility = Visibility.Visible;

        RecoveryConfirmLabel.Visibility = Visibility.Collapsed;
        RecoveryConfirmTextBox.Visibility = Visibility.Collapsed;
        ConfirmRecoveryButton.Content = "由 Host 确认恢复密钥";

        RestoreCredentialTypeComboBox.Visibility = Visibility.Collapsed;
        RestorePasswordBox.Visibility = Visibility.Collapsed;
        RestoreRecoveryKeyBox.Visibility = Visibility.Collapsed;
        HostRestoreCredentialHintText.Visibility = Visibility.Visible;

        MaintenancePasswordLabel.Visibility = Visibility.Collapsed;
        MaintenancePasswordBox.Visibility = Visibility.Collapsed;
        NewPasswordLabel.Visibility = Visibility.Collapsed;
        NewPasswordBox.Visibility = Visibility.Collapsed;
        ConfirmNewPasswordLabel.Visibility = Visibility.Collapsed;
        ConfirmNewPasswordBox.Visibility = Visibility.Collapsed;
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
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed || !DocumentWorkspace.HasUnsavedChanges)
        {
            return;
        }
        e.Cancel = true;
        if (await DocumentWorkspace.ConfirmDiscardAsync())
        {
            _closingConfirmed = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(Close));
        }
    }

    private async void InitializeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var path = VaultPathTextBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(path) ||
            (!_service.PromptsForCredentials && string.IsNullOrEmpty(password)))
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
        if (string.IsNullOrWhiteSpace(path) ||
            (!_service.PromptsForCredentials && string.IsNullOrEmpty(credential)))
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
        if (!await DocumentWorkspace.ConfirmDiscardAsync())
        {
            return;
        }
        DocumentWorkspace.Clear();
        await RunBusyAsync(async () =>
        {
            await _service.LockAsync();
            await RefreshStatusAsync();
            ClearSensitiveUi();
        });
    }

    private async void ExitAndLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        if (!await DocumentWorkspace.ConfirmDiscardAsync())
        {
            return;
        }
        DocumentWorkspace.Clear();
        await RunBusyAsync(async () =>
        {
            await _service.LockAsync("desktop-exit");
            _statusTimer.Stop();
            Close();
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
        if (!_service.PromptsForCredentials && string.IsNullOrEmpty(recoveryKey))
        {
            ShowInfo("请重新输入恢复密钥，确认已保存。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.ConfirmRecoveryKeyAsync(
                VaultPathTextBox.Text.Trim(),
                recoveryKey);
            RecoveryPanel.Visibility = Visibility.Collapsed;
            RecoveryKeyTextBox.Clear();
            RecoveryConfirmTextBox.Clear();
            await RefreshStatusAsync();
        });
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _busy || _suppressMainTabChange || e.Source != MainTabs)
        {
            return;
        }
        var targetIndex = MainTabs.SelectedIndex;
        if (_lastMainTabIndex == 1 &&
            targetIndex != 1 &&
            !await DocumentWorkspace.ConfirmDiscardAsync())
        {
            _suppressMainTabChange = true;
            MainTabs.SelectedIndex = _lastMainTabIndex;
            _suppressMainTabChange = false;
            return;
        }
        if (_lastMainTabIndex == 1 && targetIndex != 1)
        {
            DocumentWorkspace.Clear();
            _activeDocumentId = null;
        }
        _lastMainTabIndex = targetIndex;
        if (targetIndex == 1)
        {
            await RunBusyAsync(RefreshCatalogAsync);
        }
        else if (targetIndex == 2)
        {
            await RunBusyAsync(RefreshTrashAsync);
        }
    }

    private async void RefreshCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(RefreshCatalogAsync);
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
            await RunBusyAsync(RefreshCatalogAsync);
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
                FormatSearchCoverage(page.Coverage),
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
                    FormatSearchCoverage(page.Coverage),
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
        if (_busy || _suppressCatalogSelection || CatalogGrid.SelectedItem is null)
        {
            return;
        }
        var id = GetDocumentId(CatalogGrid.SelectedItem);
        if (id == Guid.Empty)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            if (await DocumentWorkspace.LoadDocumentAsync(id))
            {
                _activeDocumentId = id;
                return;
            }
            _suppressCatalogSelection = true;
            CatalogGrid.SelectedItem = CatalogGrid.Items
                .Cast<object>()
                .FirstOrDefault(item => GetDocumentId(item) == _activeDocumentId);
            _suppressCatalogSelection = false;
        });
    }

    private async void RefreshTrashButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(RefreshTrashAsync);
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
                null);
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
            await _service.PurgeNoteAsync(item.DocumentId);
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
                ImportSourceTextBox.Text.Trim());
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
                _importJobId.Value);
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
                ExportDestinationTextBox.Text.Trim());
            PrintJson(result);
            ShowAuditWarning(result.AuditRecorded, "导出");
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
                dialog.FileName);
            RestoreBackupTextBox.Text = result.BackupPath;
            PrintJson(result);
            ShowAuditWarning(result.AuditRecorded, "备份");
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
        if (!_service.PromptsForCredentials && string.IsNullOrEmpty(credential))
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
                credentialType);
            PrintJson(result);
            ShowAuditWarning(result.AuditRecorded, "恢复");
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
            var count = await _service.RebuildSearchIndexAsync();
            MaintenanceOutput.Text = $"搜索索引行数：{count}";
        });
    }

    private async void RebuildLinksButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureUnlockedAsync())
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await _service.RebuildLinkIndexAsync();
            MaintenanceOutput.Text =
                $"链接索引：{result.DocumentsIndexed} 篇文档，" +
                $"{result.LinksIndexed} 条链接";
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

    private async void StopHostButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            MessageBox.Show(
                this,
                "停止 Host 会锁定资料库并关闭所有 Agent 访问，继续吗？",
                "停止 Host",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        await StopHostAsync();
    }

    private async Task StopHostAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _service.StopHostAsync("desktop-host-stop");
            _statusTimer.Stop();
            Close();
        });
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var current = MaintenancePasswordBox.Password;
        var next = NewPasswordBox.Password;
        var confirmation = ConfirmNewPasswordBox.Password;
        if (!_service.PromptsForCredentials &&
            (string.IsNullOrEmpty(current) ||
             !string.Equals(next, confirmation, StringComparison.Ordinal)))
        {
            ShowInfo("请输入当前密码，并确认两次新密码一致。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            await _service.ChangePasswordAsync(
                VaultPathTextBox.Text.Trim(),
                current,
                next);
            MaintenancePasswordBox.Clear();
            NewPasswordBox.Clear();
            ConfirmNewPasswordBox.Clear();
            MaintenanceOutput.Text = "密码已修改。";
        });
    }

    private async void RotateRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        var current = MaintenancePasswordBox.Password;
        if (!_service.PromptsForCredentials && string.IsNullOrEmpty(current))
        {
            ShowInfo("请输入当前密码。");
            return;
        }
        await RunBusyAsync(async () =>
        {
            var recoveryKey = await _service.RotateRecoveryKeyAsync(
                VaultPathTextBox.Text.Trim(),
                current);
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
                if (_service is NamedPipeHostVaultClient)
                {
                    StatusText.Text = GetStateText(status.State);
                    UnlockButton.IsEnabled = status.BrokerAvailable;
                    InitializeButton.IsEnabled = status.BrokerAvailable;
                    SessionDetailText.Text = status.BrokerAvailable
                        ? "资料库由 strict 服务管理。请点击“解锁”，凭据将由本地授权程序收集。"
                        : "正在等待本地授权程序连接 strict 服务。";
                    ClearSensitiveUi();
                    return;
                }
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
                UnlockButton.IsEnabled = true;
                InitializeButton.IsEnabled = true;
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
        _activeDocumentId = null;
        CatalogGrid.ItemsSource = null;
        TrashGrid.ItemsSource = null;
        DocumentWorkspace.Clear();
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

    private static Guid GetDocumentId(object item)
    {
        return item switch
        {
            VaultCatalogItem catalog => catalog.DocumentId,
            VaultSearchHit hit => hit.DocumentId,
            _ => Guid.Empty
        };
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

    private static string FormatSearchCoverage(VaultSearchCoverage coverage)
    {
        var summary =
            $"匹配 {coverage.MatchedDocuments} 篇，" +
            $"扫描 {coverage.ScannedDocuments}/{coverage.TotalDocuments} 篇";
        return coverage.IsTruncated
            ? summary + "，结果可能不完整，请补充更具体的查询"
            : summary;
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

    private void ShowAuditWarning(bool auditRecorded, string operation)
    {
        if (!auditRecorded)
        {
            ShowInfo(
                $"{operation}已完成，但审计事件未记录。" +
                "请保留操作结果，并检查资料库存储状态。");
        }
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
