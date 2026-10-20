using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PersonalVault.Auth.Win;
using PersonalVault.Core;
using PersonalVault.Core.Documents;
using PersonalVault.Core.Errors;
using PersonalVault.Core.ImportExport;
using PersonalVault.Core.Session;

namespace PersonalVault.Ui.Wpf;

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
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _service = new VaultService(new WindowsSystemActivityProvider());
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "PersonalVault");
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
            ShowInfo("Vault folder and password are required.");
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
            ShowInfo("Vault folder and password are required.");
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
            CatalogGrid.ItemsSource = null;
            TrashGrid.ItemsSource = null;
            DocumentViewer.Clear();
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
            ShowInfo("Re-enter the recovery key to confirm it was saved.");
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
            CatalogGrid.ItemsSource = page.Hits;
            MaintenanceOutput.Text = $"matched={page.Coverage.MatchedDocuments}, scanned={page.Coverage.ScannedDocuments}";
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
                $"{document.Title}\n{document.VirtualPath}\nrevision {document.Revision}\n\n{document.Content}" +
                (document.Done ? string.Empty : "\n\n[... use the API to request the next offset ...]");
        });
    }

    private async void RefreshTrashButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshTrashAsync();
    }

    private async void RestoreTrashButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrashGrid.SelectedItem is not VaultTrashItem item)
        {
            ShowInfo("Select a document to restore.");
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
            ShowInfo("Select a document to permanently delete.");
            return;
        }
        if (MessageBox.Show(
                this,
                $"Permanently delete '{item.Title}'? This cannot be undone from the Vault.",
                "Permanent delete",
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
        var dialog = new OpenFolderDialog { Title = "Select a folder to import" };
        if (dialog.ShowDialog(this) == true)
        {
            ImportSourceTextBox.Text = dialog.FolderName;
        }
    }

    private void BrowseExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select an export parent folder" };
        if (dialog.ShowDialog(this) == true)
        {
            ExportDestinationTextBox.Text = Path.Combine(
                dialog.FolderName,
                $"VaultExport-{DateTime.Now:yyyyMMdd-HHmmss}");
        }
    }

    private void BrowseRestoreTargetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a restore parent folder" };
        if (dialog.ShowDialog(this) == true)
        {
            RestoreTargetTextBox.Text = Path.Combine(
                dialog.FolderName,
                $"VaultRestore-{DateTime.Now:yyyyMMdd-HHmmss}");
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
            ShowInfo("Preview an import first.");
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
            Filter = "Personal Vault backup (*.pvbackup)|*.pvbackup",
            DefaultExt = ".pvbackup",
            AddExtension = true,
            FileName = $"PersonalVault-{DateTime.Now:yyyyMMdd}.pvbackup"
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
            Filter = "Personal Vault backup (*.pvbackup)|*.pvbackup|All files (*.*)|*.*"
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
            ShowInfo("Enter the backup password.");
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
            MaintenanceOutput.Text = $"fts_rows={count}";
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
            ShowInfo("Enter the current password and matching new passwords.");
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
            MaintenanceOutput.Text = "Password changed.";
        });
    }

    private async void RotateRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        var current = MaintenancePasswordBox.Password;
        if (string.IsNullOrEmpty(current))
        {
            ShowInfo("Enter the current password.");
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
            MaintenanceOutput.Text = "Recovery key rotated. Save and confirm the new key.";
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
                    "Confirm the recovery key first. If the key was not saved, rotate recovery with the current password.");
                return false;
            }
            return true;
        }
        ShowInfo("Unlock the Vault first.");
        MainTabs.SelectedIndex = 0;
        return false;
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _service.GetStatusAsync();
            StatusText.Text = status.State.ToString().ToUpperInvariant();
            if (status.State == VaultSessionState.Locked)
            {
                SessionDetailText.Text = "Vault is locked. Enter the password above and click Unlock.";
            }
            else if (!status.RecoveryVerified)
            {
                StatusText.Text = "RECOVERY UNCONFIRMED";
                SessionDetailText.Text =
                    "Confirm the recovery key, or rotate recovery with the current password, before accessing content.";
            }
            else
            {
                SessionDetailText.Text =
                    $"Session {status.SessionId} | idle until {status.IdleExpiresAt:u} | hard stop {status.HardExpiresAt:u}";
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
            CatalogGrid.ItemsSource = null;
            return;
        }
        var page = await _service.ListCatalogAsync(null, null, 100);
        CatalogGrid.ItemsSource = page.Items;
    }

    private async Task RefreshTrashAsync()
    {
        var status = await _service.GetStatusAsync();
        if (status.State == VaultSessionState.Locked || !status.RecoveryVerified)
        {
            TrashGrid.ItemsSource = null;
            return;
        }
        var page = await _service.ListTrashAsync(null, 100);
        TrashGrid.ItemsSource = page.Items;
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
            ShowInfo($"{exception.Code}: {exception.Message}");
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
        MessageBox.Show(this, message, "Personal Vault", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string NormalizeRecoveryKey(string value)
    {
        return new string(
            value
                .Where(character => !char.IsWhiteSpace(character) && character != '-')
                .Select(char.ToUpperInvariant)
                .ToArray());
    }
}
