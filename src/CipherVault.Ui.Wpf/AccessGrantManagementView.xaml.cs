using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;

namespace CipherVault.Ui.Wpf;

public partial class AccessGrantManagementView : UserControl
{
    private IHostVaultClient? _service;
    private string? _nextCursor;
    private bool _busy;
    private bool _hasAccess;
    private bool _loadedOnce;
    private AdminAccessGrantDetail? _selectedGrant;
    private int _accessGeneration;

    public AccessGrantManagementView()
    {
        InitializeComponent();
    }

    public AdminAccessGrantDetail? SelectedGrant => _selectedGrant;

    public void Initialize(IHostVaultClient service)
    {
        _service = service;
    }

    public void SetAccessAvailable(bool available)
    {
        _hasAccess = available;
        SearchButton.IsEnabled = available;
        RefreshButton.IsEnabled = available;
        StateFilterComboBox.IsEnabled = available;
        SearchTextBox.IsEnabled = available;
        if (available)
        {
            VaultStateText.Text = "授权状态由 Host 提供";
            UpdateRevokeButton();
            UpdateRevokeAllButton();
            return;
        }
        Clear("Vault 已锁定，解锁后可管理授权。");
    }

    public async Task RefreshAsync()
    {
        if (_busy || !_hasAccess || _service is null)
        {
            return;
        }
        await LoadPageAsync(reset: true);
    }

    public void Clear(string message = "Vault 已锁定。")
    {
        _accessGeneration++;
        _nextCursor = null;
        _loadedOnce = false;
        GrantsGrid.ItemsSource = null;
        _selectedGrant = null;
        ClearDetails();
        RevokeButton.IsEnabled = false;
        RevokeAllButton.IsEnabled = false;
        LoadMoreButton.IsEnabled = false;
        StatusText.Text = message;
        VaultStateText.Text = message;
        SetCounts(new AdminAccessGrantStatusCounts(0, 0, 0, 0));
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(() => LoadPageAsync(reset: true));
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(() => LoadPageAsync(reset: true));
    }

    private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nextCursor is null)
        {
            return;
        }
        await RunBusyAsync(() => LoadPageAsync(reset: false));
    }

    private async void StateFilterComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_loadedOnce || _busy)
        {
            return;
        }
        await RunBusyAsync(() => LoadPageAsync(reset: true));
    }

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        e.Handled = true;
        await RunBusyAsync(() => LoadPageAsync(reset: true));
    }

    private async void GrantsGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (GrantsGrid.SelectedItem is not AccessGrantRow row ||
            _service is null ||
            !_hasAccess)
        {
            _selectedGrant = null;
            ClearDetails();
            return;
        }
        try
        {
            var generation = _accessGeneration;
            var detail = await _service.GetAccessGrantAsync(row.GrantId);
            if (generation != _accessGeneration || !_hasAccess)
            {
                return;
            }
            _selectedGrant = detail;
            ShowDetails(detail);
            UpdateRevokeButton();
        }
        catch (Exception exception)
        {
            _selectedGrant = null;
            ClearDetails();
            StatusText.Text = $"加载授权详情失败：{exception.Message}";
            UpdateRevokeButton();
        }
    }

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null ||
            _selectedGrant is null ||
            _busy ||
            !string.Equals(
                _selectedGrant.State,
                "active",
                StringComparison.Ordinal))
        {
            return;
        }
        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"确认撤销 {_selectedGrant.ClientDisplayName} " +
            $"({_selectedGrant.ClientId}) 的授权？\n" +
            $"权限：{string.Join("、", _selectedGrant.Operations)}\n" +
            $"范围：{string.Join("、", _selectedGrant.ResourceScopes)}",
            "撤销授权",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        await RunBusyAsync(async () =>
        {
            var outcome = await _service.RevokeAccessGrantAsync(
                _selectedGrant.GrantId);
            _selectedGrant = null;
            ClearDetails();
            await LoadPageAsync(reset: true);
            StatusText.Text = outcome.Outcome switch
            {
                "revoked" => "授权已撤销。",
                "alreadyRevoked" => "授权此前已撤销。",
                "alreadyExpired" => "授权已过期，无需撤销。",
                "cancelled" => "待审批授权已取消。",
                _ => "Host 返回了未知的撤销结果。"
            };
        });
    }

    private async void RevokeAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _busy || !_hasAccess)
        {
            return;
        }
        try
        {
            var preview = await _service.PreviewRevokeAllAccessGrantsAsync();
            if (preview.ActiveCount == 0 && preview.PendingCount == 0)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    "当前没有需要撤销的授权。",
                    "撤销全部授权",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            var impacts = string.Join(
                "\n",
                preview.Impacts.Select(item =>
                    $"{item.ClientDisplayName} ({item.ClientId})：" +
                    $"Active {item.ActiveCount}，Pending {item.PendingCount}，" +
                    $"{string.Join("、", item.Operations)} / " +
                    $"{string.Join("、", item.ResourceScopes)}" +
                    (item.IsTruncated ? "（范围已截断）" : string.Empty)));
            var result = MessageBox.Show(
                Window.GetWindow(this),
                $"确认撤销全部目标授权？\n" +
                $"Active {preview.ActiveCount}，Pending {preview.PendingCount}\n" +
                impacts,
                "撤销全部授权",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
            await RunBusyAsync(async () =>
            {
                var revoked = await _service.RevokeAllAccessGrantsAsync(
                    preview.PreviewToken);
                _selectedGrant = null;
                ClearDetails();
                await LoadPageAsync(reset: true);
                StatusText.Text =
                    $"撤销完成：Active {revoked.RevokedCount}，" +
                    $"Pending {revoked.CancelledPendingCount}，" +
                    $"已撤销 {revoked.AlreadyRevokedCount}，" +
                    $"已过期 {revoked.AlreadyExpiredCount}。";
            });
        }
        catch (Exception exception)
        {
            StatusText.Text = $"撤销全部失败：{exception.Message}";
        }
    }

    private async Task LoadPageAsync(bool reset)
    {
        if (_service is null || !_hasAccess)
        {
            return;
        }
        var state = (StateFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var generation = _accessGeneration;
        var page = await _service.ListAccessGrantsAsync(
            state,
            SearchTextBox.Text.Trim(),
            reset ? null : _nextCursor,
            50);
        if (generation != _accessGeneration || !_hasAccess)
        {
            return;
        }
        var rows = page.Items.Select(AccessGrantRow.From).ToList();
        if (!reset)
        {
            rows.InsertRange(
                0,
                (GrantsGrid.ItemsSource as IEnumerable<AccessGrantRow>) ?? []);
        }
        GrantsGrid.ItemsSource = rows;
        _nextCursor = page.NextCursor;
        _loadedOnce = true;
        LoadMoreButton.IsEnabled = page.NextCursor is not null;
        SetCounts(page.Counts);
        StatusText.Text = rows.Count == 0
            ? "没有匹配的授权记录。"
            : $"当前显示 {rows.Count} 条，匹配总数 {page.TotalCount} 条。";
        VaultStateText.Text = "授权状态由 Host 提供";
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        SearchButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        LoadMoreButton.IsEnabled = false;
        RevokeAllButton.IsEnabled = false;
        RevokeButton.IsEnabled = false;
        StatusText.Text = "正在加载授权...";
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"加载授权失败：{exception.Message}";
            VaultStateText.Text = string.Empty;
        }
        finally
        {
            _busy = false;
            if (_hasAccess)
            {
                SearchButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                LoadMoreButton.IsEnabled = _nextCursor is not null;
                UpdateRevokeButton();
                UpdateRevokeAllButton();
            }
        }
    }

    private void SetCounts(AdminAccessGrantStatusCounts counts)
    {
        TotalCountText.Text = $"总数 {counts.Total}";
        ActiveCountText.Text = $"有效 {counts.Active}";
        RevokedCountText.Text = $"已撤销 {counts.Revoked}";
        ExpiredCountText.Text = $"已过期 {counts.Expired}";
        UpdateRevokeAllButton();
    }

    private void ShowDetails(AdminAccessGrantDetail grant)
    {
        DetailGrantIdText.Text = grant.GrantId.ToString("D");
        DetailClientText.Text =
            $"{grant.ClientDisplayName} ({grant.ClientId})";
        DetailStateText.Text = grant.State switch
        {
            "active" => "有效",
            "revoked" => "已撤销",
            "expired" => "已过期",
            _ => grant.State
        };
        DetailTimeText.Text =
            $"创建：{grant.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
            $"到期：{grant.HardExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
            $"最后使用：{grant.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "无"}";
        DetailOperationsText.Text =
            $"请求：{string.Join("、", grant.RequestedOperations)}\n" +
            $"实际：{string.Join("、", grant.Operations)}";
        DetailScopesText.Text = string.Join("、", grant.ResourceScopes);
        DetailRevocationText.Text = grant.RevokedAt is null
            ? "无"
            : $"{grant.RevokedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
                $"({grant.RevocationReason ?? "unknown"})";
    }

    private void ClearDetails()
    {
        DetailGrantIdText.Text = "未选择";
        DetailClientText.Text = "未选择";
        DetailStateText.Text = "未选择";
        DetailTimeText.Text = "-";
        DetailOperationsText.Text = "-";
        DetailScopesText.Text = "-";
        DetailRevocationText.Text = "-";
    }

    private void UpdateRevokeButton()
    {
        RevokeButton.IsEnabled =
            !_busy &&
            _hasAccess &&
            _selectedGrant is not null &&
            string.Equals(
                _selectedGrant.State,
                "active",
                StringComparison.Ordinal);
    }

    private void UpdateRevokeAllButton()
    {
        RevokeAllButton.IsEnabled =
            !_busy &&
            _hasAccess;
    }
}

public sealed record AccessGrantRow(
    Guid GrantId,
    string ClientId,
    string ClientDisplayName,
    string State,
    string StateText,
    string OperationsText,
    string ResourceScopesText,
    string CreatedAtText,
    string HardExpiresAtText,
    string LastUsedAtText)
{
    public static AccessGrantRow From(AdminAccessGrantListItem item)
    {
        return new AccessGrantRow(
            item.GrantId,
            item.ClientId,
            item.ClientDisplayName,
            item.State,
            item.State switch
            {
                "active" => "有效",
                "revoked" => "已撤销",
                "expired" => "已过期",
                _ => item.State
            },
            string.Join("、", item.Operations),
            string.Join("、", item.ResourceScopes),
            item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            item.HardExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            item.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "无");
    }
}
