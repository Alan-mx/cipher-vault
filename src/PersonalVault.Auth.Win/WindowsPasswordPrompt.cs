using System.Windows;
using System.Windows.Controls;

namespace PersonalVault.Auth.Win;

public sealed record PasswordPromptResult(bool Confirmed, string? Password);

public static class WindowsPasswordPrompt
{
    private static readonly object ApplicationGate = new();
    private static Application? _application;

    public static Task<PasswordPromptResult> RequestAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new PasswordPromptResult(false, null));
        }

        var application = EnsureApplication();
        var completion = new TaskCompletionSource<PasswordPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ = application.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var window = new PasswordPromptWindow(title, message);
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
                Name = "PersonalVault.AuthDispatcher"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (startupException is not null || _application is null)
            {
                throw new InvalidOperationException(
                    "Unable to start the Windows authentication dispatcher.",
                    startupException);
            }

            return _application;
        }
    }

    private sealed class PasswordPromptWindow : Window
    {
        private readonly PasswordBox _passwordBox;
        private bool _confirmed;

        public PasswordPromptWindow(string title, string message)
        {
            Title = title;
            Width = 460;
            Height = 220;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;

            _passwordBox = new PasswordBox
            {
                MinHeight = 28,
                Margin = new Thickness(0, 8, 0, 12)
            };

            var approveButton = new Button
            {
                Content = "Unlock",
                MinWidth = 90,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            approveButton.Click += (_, _) => Confirm();

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                IsCancel = true
            };
            cancelButton.Click += (_, _) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(approveButton);
            buttons.Children.Add(cancelButton);

            var panel = new StackPanel
            {
                Margin = new Thickness(20)
            };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Vault password:",
                Margin = new Thickness(0, 12, 0, 0)
            });
            panel.Children.Add(_passwordBox);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, _) => _passwordBox.Focus();
        }

        public PasswordPromptResult Result => new(_confirmed, _confirmed ? _passwordBox.Password : null);

        private void Confirm()
        {
            if (string.IsNullOrEmpty(_passwordBox.Password))
            {
                return;
            }

            _confirmed = true;
            Close();
        }
    }
}
