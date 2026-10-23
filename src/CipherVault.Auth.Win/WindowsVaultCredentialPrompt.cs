using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CipherVault.Core.Crypto;
using CipherVault.Core.Session;

namespace CipherVault.Auth.Win;

public sealed record VaultCredentialPromptResult(
    bool Confirmed,
    string? Credential,
    VaultCredentialType CredentialType);

public static class WindowsVaultCredentialPrompt
{
    public static Task<VaultCredentialPromptResult> RequestAsync(
        string title,
        string message,
        VaultCredentialType? preferredType = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new VaultCredentialPromptResult(
                false,
                null,
                preferredType ?? VaultCredentialType.Password));
        }

        var application = WindowsPromptApplication.Get();
        var completion = new TaskCompletionSource<VaultCredentialPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ = application.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var window = new VaultCredentialPromptWindow(
                    title,
                    message,
                    preferredType);
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
    private sealed class VaultCredentialPromptWindow : Window
    {
        private readonly PasswordBox _credentialBox;
        private readonly ComboBox? _typeSelector;
        private readonly TextBlock _credentialLabel;
        private readonly TextBlock _validationMessage;
        private bool _confirmed;

        public VaultCredentialPromptWindow(
            string title,
            string message,
            VaultCredentialType? preferredType)
        {
            Title = title;
            Width = 480;
            Height = preferredType is null ? 290 : 250;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;

            _credentialBox = new PasswordBox
            {
                MinHeight = 28,
                Margin = new Thickness(0, 6, 0, 10)
            };
            _credentialLabel = new TextBlock
            {
                Text = "资料库密码：",
                Margin = new Thickness(0, 10, 0, 0)
            };
            _validationMessage = new TextBlock
            {
                Foreground = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var approveButton = new Button
            {
                Content = "解锁",
                MinWidth = 90,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            approveButton.Click += (_, _) => Confirm();

            var cancelButton = new Button
            {
                Content = "取消",
                MinWidth = 90,
                IsCancel = true
            };
            cancelButton.Click += (_, _) => Close();
            var pasteButton = new Button
            {
                Content = "粘贴",
                MinWidth = 90,
                Margin = new Thickness(0, 0, 8, 0)
            };
            pasteButton.Click += (_, _) => PasteCredential();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(pasteButton);
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

            if (preferredType is null)
            {
                _typeSelector = new ComboBox
                {
                    MinHeight = 28,
                    Margin = new Thickness(0, 10, 0, 4),
                    SelectedIndex = 0
                };
                _typeSelector.Items.Add(new ComboBoxItem { Content = "密码" });
                _typeSelector.Items.Add(new ComboBoxItem { Content = "恢复密钥" });
                _typeSelector.SelectionChanged += (_, _) => UpdateCredentialLabel();
                panel.Children.Add(_typeSelector);
            }
            else
            {
                _initialType = preferredType.Value;
            }

            panel.Children.Add(_credentialLabel);
            panel.Children.Add(_credentialBox);
            panel.Children.Add(_validationMessage);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, _) => _credentialBox.Focus();
            UpdateCredentialLabel();
        }

        public VaultCredentialPromptResult Result => new(
            _confirmed,
            _confirmed ? _credentialBox.Password : null,
            SelectedType);

        private VaultCredentialType SelectedType => _initialType;

        private VaultCredentialType _initialType { get; set; } =
            VaultCredentialType.Password;

        private void UpdateCredentialLabel()
        {
            if (_typeSelector is not null)
            {
                _initialType = _typeSelector.SelectedIndex == 1
                    ? VaultCredentialType.RecoveryKey
                    : VaultCredentialType.Password;
            }
            _credentialLabel.Text =
                _initialType == VaultCredentialType.RecoveryKey
                    ? "恢复密钥："
                    : "资料库密码：";
        }

        private void Confirm()
        {
            if (string.IsNullOrEmpty(_credentialBox.Password))
            {
                return;
            }
            _validationMessage.Text = string.Empty;
            if (_initialType == VaultCredentialType.RecoveryKey)
            {
                try
                {
                    _ = RecoveryKeyCodec.Parse(_credentialBox.Password);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException or
                    CipherVault.Core.Errors.VaultException)
                {
                    _validationMessage.Text = exception.Message;
                    _credentialBox.Focus();
                    return;
                }
            }

            _confirmed = true;
            Close();
        }

        private void PasteCredential()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    _credentialBox.Password = Clipboard.GetText().Trim();
                    _credentialBox.Focus();
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.Runtime.InteropServices.COMException)
            {
                _validationMessage.Text = "无法读取剪贴板。";
            }
        }
    }
}
