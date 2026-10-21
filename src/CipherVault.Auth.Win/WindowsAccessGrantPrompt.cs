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
    bool Delete);

public static class WindowsAccessGrantPrompt
{
    private static readonly object ApplicationGate = new();
    private static Application? _application;

    public static Task<AccessGrantPromptResult> RequestAsync(
        string title,
        AccessGrantPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new AccessGrantPromptResult(false, false, false, false));
        }

        var application = EnsureApplication();
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

    private static Application EnsureApplication()
    {
        lock (ApplicationGate)
        {
            if (_application is not null)
            {
                return _application;
            }

            var ready = new ManualResetEventSlim(false);
            Exception? startupException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    _application = new Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    ready.Set();
                    _application.Run();
                }
                catch (Exception exception)
                {
                    startupException = exception;
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "CipherVault.AuthDispatcher"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (startupException is not null || _application is null)
            {
                throw new InvalidOperationException(
                    "无法启动 Windows 授权窗口。",
                    startupException);
            }

            return _application;
        }
    }

    private sealed class AccessGrantPromptWindow : Window
    {
        private readonly CheckBox _readBox;
        private readonly CheckBox _writeBox;
        private readonly CheckBox _deleteBox;
        private bool _confirmed;

        public AccessGrantPromptWindow(
            string title,
            AccessGrantPromptRequest request)
        {
            Title = title;
            Width = 480;
            Height = 340;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;

            var allowRead = request.AllowedScopes.Contains("read");
            var allowWrite = request.AllowedScopes.Contains("write");
            var allowDelete = request.AllowedScopes.Contains("delete");
            var requestRead = request.RequestedScopes.Contains("read");
            var requestWrite = request.RequestedScopes.Contains("write");
            var requestDelete = request.RequestedScopes.Contains("delete");

            _readBox = CreateScopeCheckBox(
                "读取（read）",
                allowRead,
                allowRead && requestRead);
            _writeBox = CreateScopeCheckBox(
                "写入（write）",
                allowWrite,
                allowWrite && requestWrite);
            _deleteBox = CreateScopeCheckBox(
                "删除（delete）",
                allowDelete,
                false);

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
            Content = panel;
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

        private void Confirm()
        {
            _confirmed = true;
            Close();
        }
    }
}
