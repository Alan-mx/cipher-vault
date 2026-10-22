using System.Windows;

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
                var result = MessageBox.Show(
                    details,
                    title,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                completion.TrySetResult(
                    new OperationApprovalPromptResult(
                        result == MessageBoxResult.Yes));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }
}
