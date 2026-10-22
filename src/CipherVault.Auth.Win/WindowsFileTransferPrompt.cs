using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CipherVault.Auth.Win;

public sealed record FileTransferPromptRequest(
    Guid TransferId,
    string Direction,
    string PayloadKind,
    string StagingPath,
    string SuggestedPath,
    DateTimeOffset ExpiresAt);

public sealed record FileTransferPromptResult(
    Guid TransferId,
    bool Completed,
    string? FinalPath,
    string? ErrorCode);

public static class WindowsFileTransferPrompt
{
    public static Task<FileTransferPromptResult> RequestAsync(
        FileTransferPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new FileTransferPromptResult(
                request.TransferId,
                false,
                null,
                "transfer_cancelled"));
        }
        var application = WindowsPromptApplication.Get();
        var completion = new TaskCompletionSource<FileTransferPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = application.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                completion.TrySetResult(Execute(request, cancellationToken));
            }
            catch (Exception exception)
            {
                completion.TrySetResult(new FileTransferPromptResult(
                    request.TransferId,
                    false,
                    null,
                    exception is UnauthorizedAccessException
                        ? "transfer_access_denied"
                        : "transfer_failed"));
            }
        });
        return completion.Task;
    }

    private static FileTransferPromptResult Execute(
        FileTransferPromptRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new FileTransferPromptResult(
                request.TransferId,
                false,
                null,
                "transfer_expired");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(request);
        }
        var direction = request.Direction.ToLowerInvariant();
        var payloadKind = request.PayloadKind.ToLowerInvariant();
        if (direction == "export")
        {
            return payloadKind == "file"
                ? ExportFile(request, cancellationToken)
                : ExportDirectory(request, cancellationToken);
        }
        if (direction == "import")
        {
            return payloadKind == "file"
                ? ImportFile(request, cancellationToken)
                : ImportDirectory(request, cancellationToken);
        }
        return new FileTransferPromptResult(
            request.TransferId,
            false,
            null,
            "transfer_invalid");
    }

    private static FileTransferPromptResult ExportFile(
        FileTransferPromptRequest request,
        CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存 CipherVault 备份",
            FileName = Path.GetFileName(request.SuggestedPath),
            InitialDirectory = Path.GetDirectoryName(request.SuggestedPath),
            OverwritePrompt = false
        };
        if (dialog.ShowDialog() != true ||
            cancellationToken.IsCancellationRequested)
        {
            return Cancelled(request);
        }
        if (File.Exists(dialog.FileName))
        {
            return TargetExists(request);
        }
        File.Copy(request.StagingPath, dialog.FileName, overwrite: false);
        return Completed(request, dialog.FileName);
    }

    private static FileTransferPromptResult ExportDirectory(
        FileTransferPromptRequest request,
        CancellationToken cancellationToken)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 CipherVault 导出位置",
            InitialDirectory = Path.GetDirectoryName(request.SuggestedPath)
        };
        if (dialog.ShowDialog() != true ||
            cancellationToken.IsCancellationRequested)
        {
            return Cancelled(request);
        }
        var destination = Path.Combine(
            dialog.FolderName,
            Path.GetFileName(request.SuggestedPath));
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return TargetExists(request);
        }
        CopyDirectory(request.StagingPath, destination);
        return Completed(request, destination);
    }

    private static FileTransferPromptResult ImportFile(
        FileTransferPromptRequest request,
        CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的 CipherVault 文件",
            InitialDirectory = Directory.Exists(request.SuggestedPath)
                ? request.SuggestedPath
                : Path.GetDirectoryName(request.SuggestedPath),
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true ||
            cancellationToken.IsCancellationRequested)
        {
            return Cancelled(request);
        }
        if (File.Exists(request.StagingPath))
        {
            return TargetExists(request);
        }
        File.Copy(dialog.FileName, request.StagingPath, overwrite: false);
        return Completed(request, request.StagingPath);
    }

    private static FileTransferPromptResult ImportDirectory(
        FileTransferPromptRequest request,
        CancellationToken cancellationToken)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要导入的 CipherVault 文件夹",
            InitialDirectory = Directory.Exists(request.SuggestedPath)
                ? request.SuggestedPath
                : null
        };
        if (dialog.ShowDialog() != true ||
            cancellationToken.IsCancellationRequested)
        {
            return Cancelled(request);
        }
        CopyDirectory(dialog.FolderName, request.StagingPath);
        return Completed(request, request.StagingPath);
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceRoot = Path.GetFullPath(source);
        EnsureNoReparsePoints(sourceRoot);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Source contains a reparse point.");
            }
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(sourceRoot, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Source contains a reparse point.");
            }
            var target = Path.Combine(
                destination,
                Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void EnsureNoReparsePoints(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Source contains a reparse point.");
            }
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Source is a reparse point.");
        }
    }

    private static FileTransferPromptResult TargetExists(
        FileTransferPromptRequest request)
    {
        return new FileTransferPromptResult(
            request.TransferId,
            false,
            null,
            "transfer_target_exists");
    }

    private static FileTransferPromptResult Cancelled(
        FileTransferPromptRequest request)
    {
        return new FileTransferPromptResult(
            request.TransferId,
            false,
            null,
            "transfer_cancelled");
    }

    private static FileTransferPromptResult Completed(
        FileTransferPromptRequest request,
        string path)
    {
        return new FileTransferPromptResult(
            request.TransferId,
            true,
            Path.GetFullPath(path),
            null);
    }
}
