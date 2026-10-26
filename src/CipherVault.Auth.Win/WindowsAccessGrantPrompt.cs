using System.Windows;
using System.Windows.Controls;

namespace CipherVault.Auth.Win;

public sealed record AccessGrantPromptRequest(
    string ClientId,
    Guid ClientInstanceId,
    string TokenAccessMode,
    IReadOnlySet<string> AllowedScopes,
    IReadOnlySet<string> RequestedScopes,
    string IdleLifetime,
    string HardLifetime);

public sealed record AccessGrantPromptResult(
    bool Confirmed,
    bool Read,
    bool Write,
    bool Delete)
{
    public IReadOnlyList<string> ToScopeNames()
    {
        var scopes = new List<string>(3);
        if (Read || Write || Delete)
        {
            scopes.Add("read");
        }
        if (Write)
        {
            scopes.Add("write");
        }
        if (Write && Delete)
        {
            scopes.Add("delete");
        }
        return scopes;
    }
}

public static class WindowsAccessGrantPrompt
{
    public static bool IncludesAction(
        IEnumerable<string> scopes,
        string action)
    {
        return scopes.Any(scope =>
        {
            var separator = scope.IndexOf(':');
            var value = separator < 0 ? scope : scope[..separator];
            return string.Equals(
                value.Trim(),
                action,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    public static IReadOnlyList<string> DescribeScopes(
        IEnumerable<string> scopes)
    {
        return scopes
            .Select(ParseScope)
            .Where(scope => scope.Action.Length > 0)
            .DistinctBy(scope => (scope.Action, scope.Folder))
            .OrderBy(scope => scope.Action switch
            {
                "read" => 0,
                "write" => 1,
                "delete" => 2,
                _ => 3
            })
            .ThenBy(scope => scope.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(scope =>
            {
                var action = scope.Action switch
                {
                    "read" => "允许读取",
                    "write" => "允许写入",
                    "delete" => "允许删除",
                    _ => $"未知权限 {scope.Action}"
                };
                return scope.Folder is null
                    ? $"{action}：全部文档（{scope.Action}）"
                    : $"{action}：{scope.Folder.Trim('/')}/（{scope.Action}:{scope.Folder.Trim('/')}）";
            })
            .ToArray();
    }

    public static Task<AccessGrantPromptResult> RequestAsync(
        string title,
        AccessGrantPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new AccessGrantPromptResult(false, false, false, false));
        }

        var application = WindowsPromptApplication.Get();
        var completion = new TaskCompletionSource<AccessGrantPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ = application.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var window = new AccessGrantPromptWindow(title, request);
                using var registration = cancellationToken.Register(
                    () => window.Dispatcher.BeginInvoke(window.Close));
                _ = window.ShowDialog();
                completion.TrySetResult(window.Result);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task;
    }
    private sealed class AccessGrantPromptWindow : Window
    {
        private readonly CheckBox _readBox;
        private readonly CheckBox _writeBox;
        private readonly CheckBox _deleteBox;
        private readonly bool _deleteAllowed;
        private bool _confirmed;

        public AccessGrantPromptWindow(
            string title,
            AccessGrantPromptRequest request)
        {
            Title = title;
            Width = 560;
            Height = 460;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;

            var allowRead = IncludesAction(request.AllowedScopes, "read");
            var allowWrite = IncludesAction(request.AllowedScopes, "write");
            var allowDelete = IncludesAction(request.AllowedScopes, "delete");
            var requestRead = IncludesAction(request.RequestedScopes, "read");
            var requestWrite = IncludesAction(request.RequestedScopes, "write");
            var requestDelete = IncludesAction(request.RequestedScopes, "delete");

            _readBox = CreateScopeCheckBox(
                "读取（必需）",
                enabled: false,
                allowRead && requestRead);
            _writeBox = CreateScopeCheckBox(
                "写入",
                allowWrite && requestWrite,
                allowWrite && requestWrite);
            _deleteAllowed = allowDelete && requestDelete;
            _deleteBox = CreateScopeCheckBox(
                "删除（需写入）",
                enabled: false,
                isChecked: false);
            _writeBox.Checked += (_, _) => UpdateDeleteAvailability();
            _writeBox.Unchecked += (_, _) =>
            {
                _deleteBox.IsChecked = false;
                UpdateDeleteAvailability();
            };
            UpdateDeleteAvailability();

            var approveButton = new Button
            {
                Content = "批准",
                MinWidth = 96,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            approveButton.Click += (_, _) => Confirm();
            var cancelButton = new Button
            {
                Content = "拒绝",
                MinWidth = 96,
                IsCancel = true
            };
            cancelButton.Click += (_, _) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            buttons.Children.Add(approveButton);
            buttons.Children.Add(cancelButton);

            var panel = new StackPanel
            {
                Margin = new Thickness(20)
            };
            panel.Children.Add(new TextBlock
            {
                Text = "AI 客户端请求访问 CipherVault。",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"客户端 ID：{request.ClientId}",
                Margin = new Thickness(0, 12, 0, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Bridge 实例：{request.ClientInstanceId.ToString("D")}",
                Margin = new Thickness(0, 4, 0, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Token 权限上限：{request.TokenAccessMode}",
                Margin = new Thickness(0, 4, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "本次请求的目录范围：",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            foreach (var line in DescribeScopes(request.RequestedScopes))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"• {line}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 0, 0, 3)
                });
            }
            panel.Children.Add(_readBox);
            panel.Children.Add(_writeBox);
            panel.Children.Add(_deleteBox);
            panel.Children.Add(new TextBlock
            {
                Text = $"空闲失效：{request.IdleLifetime}；最迟失效：{request.HardLifetime}",
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(buttons);
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };
            Loaded += (_, _) => approveButton.Focus();
        }

        public AccessGrantPromptResult Result => new(
            _confirmed,
            _readBox.IsChecked == true,
            _writeBox.IsChecked == true,
            _deleteBox.IsChecked == true);

        private static CheckBox CreateScopeCheckBox(
            string label,
            bool enabled,
            bool isChecked)
        {
            return new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 4, 0, 4),
                IsEnabled = enabled,
                IsChecked = isChecked
            };
        }

        private void UpdateDeleteAvailability()
        {
            var canDelete = _deleteAllowed && _writeBox.IsChecked == true;
            _deleteBox.IsEnabled = canDelete;
            if (!canDelete)
            {
                _deleteBox.IsChecked = false;
            }
        }

        private void Confirm()
        {
            _confirmed = true;
            Close();
        }
    }

    private static (string Action, string? Folder) ParseScope(string scope)
    {
        var parts = scope.Split(':', 2, StringSplitOptions.TrimEntries);
        return (
            parts[0].ToLowerInvariant(),
            parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }
}
