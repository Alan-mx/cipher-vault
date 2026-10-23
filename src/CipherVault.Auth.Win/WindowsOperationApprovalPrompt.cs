using System.Windows;
using System.Windows.Controls;

namespace CipherVault.Auth.Win;

public sealed record OperationApprovalPromptResult(
    bool Approved);

public static class WindowsOperationApprovalPrompt
{
    public static Task<OperationApprovalPromptResult> RequestAsync(
        string title,
        string details,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new OperationApprovalPromptResult(false));
        }
        var application = WindowsPromptApplication.Get();
        var completion = new TaskCompletionSource<OperationApprovalPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = application.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var window = new OperationApprovalWindow(title, details);
                using var registration = cancellationToken.Register(
                    () => window.Dispatcher.BeginInvoke(window.Close));
                _ = window.ShowDialog();
                completion.TrySetResult(
                    new OperationApprovalPromptResult(
                        window.Approved));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private sealed class OperationApprovalWindow : Window
    {
        public OperationApprovalWindow(string title, string details)
        {
            Title = title;
            Width = 480;
            SizeToContent = SizeToContent.Height;
            MinHeight = 190;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;

            var approveButton = new Button
            {
                Content = "批准",
                MinWidth = 90,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            approveButton.Click += (_, _) =>
            {
                Approved = true;
                Close();
            };

            var cancelButton = new Button
            {
                Content = "取消",
                MinWidth = 90,
                IsCancel = true
            };

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
                Text = details,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            });
            panel.Children.Add(buttons);
            Content = panel;
        }

        public bool Approved { get; private set; }
    }
}
