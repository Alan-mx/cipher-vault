using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Import;

public sealed record ImportSourceFile(
    string RelativePath,
    string VirtualPath,
    string Title,
    VaultDocumentFormat Format,
    long SizeBytes,
    string Sha256,
    string? RejectionReason);

public sealed record ImportSourceContent(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    byte[] Bytes);

public static class ImportSourceReader
{
    public const int MaxFiles = 5_000;
    public const long MaxTotalBytes = 512L * 1024 * 1024;
    private const int MaxDocumentSourceBytes =
        (256 * 1024) + 3;

    public static string CanonicalizeRoot(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw InvalidSource("Import source root is required.");
        }

        try
        {
            var fullPath = Path.GetFullPath(sourceRoot.Trim());
            if (!Directory.Exists(fullPath))
            {
                throw InvalidSource("Import source root does not exist.");
            }

            var info = new DirectoryInfo(fullPath);
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
            {
                fullPath = Path.GetFullPath(resolved.FullName);
            }

            if (!Directory.Exists(fullPath))
            {
                throw InvalidSource("Import source root is not a directory.");
            }

            return Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            throw InvalidSource("Import source root is invalid.");
        }
    }

    public static async Task<IReadOnlyList<ImportSourceFile>> ScanAsync(
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        var root = CanonicalizeRoot(canonicalRoot);
        if (!string.Equals(root, canonicalRoot, StringComparison.Ordinal))
        {
            throw InvalidSource("Import source root is not canonical.");
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        var files = new List<ImportSourceFile>();
        long totalBytes = 0;
        try
        {
            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pendingDirectories.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsUnderRoot(root, path))
                    {
                        continue;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(path);
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or System.Security.SecurityException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(path);
                        continue;
                    }

                    var format = FormatFromExtension(path);
                    if (format is null)
                    {
                        continue;
                    }

                    if (files.Count >= MaxFiles)
                    {
                        throw SourceLimitExceeded("file_count", MaxFiles);
                    }

                    long sizeBytes;
                    try
                    {
                        sizeBytes = new FileInfo(path).Length;
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or System.Security.SecurityException)
                    {
                        sizeBytes = 0;
                    }

                    totalBytes = checked(totalBytes + sizeBytes);
                    if (totalBytes > MaxTotalBytes)
                    {
                        throw SourceLimitExceeded("total_bytes", MaxTotalBytes);
                    }

                    var relativePath = ToVirtualRelativePath(root, path);
                    files.Add(await InspectFileAsync(
                        path,
                        relativePath,
                        format.Value,
                        sizeBytes,
                        cancellationToken));
                }
            }
        }
        catch (VaultException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw InvalidSource("Unable to scan the import source root.");
        }

        return files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task<ImportSourceContent> ReadAsync(
        string canonicalRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(relativePath)
            || relativePath.IndexOf('\0') >= 0
            || Path.IsPathRooted(relativePath)
            || relativePath.Replace('\\', '/').Split('/')
                .Any(segment => segment is "" or "." or ".."))
        {
            throw InvalidSource("Import source path is invalid.");
        }

        var root = CanonicalizeRoot(canonicalRoot);
        if (!string.Equals(root, canonicalRoot, StringComparison.Ordinal))
        {
            throw InvalidSource("Import source root changed after preview.");
        }
        var fullPath = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, fullPath) || !File.Exists(fullPath))
        {
            throw InvalidSource("Import source file is unavailable.");
        }

        EnsureNoReparsePoints(root, fullPath);
        var bytes = await ReadBoundedAsync(
            fullPath,
            MaxDocumentSourceBytes,
            cancellationToken);
        if (bytes is null)
        {
            throw new VaultException(
                VaultErrorCode.DocumentTooLarge,
                "Imported document exceeds the per-document source limit.");
        }

        return new ImportSourceContent(
            relativePath.Replace('\\', '/'),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes);
    }

    private static async Task<ImportSourceFile> InspectFileAsync(
        string path,
        string relativePath,
        VaultDocumentFormat format,
        long declaredSizeBytes,
        CancellationToken cancellationToken)
    {
        string virtualPath;
        string title;
        string? rejectionReason = null;
        try
        {
            virtualPath = VirtualPath.Normalize(relativePath, format).Path;
            title = Path.GetFileNameWithoutExtension(relativePath);
            if (string.IsNullOrWhiteSpace(title))
            {
                rejectionReason = "invalid_title";
            }
        }
        catch (VaultException)
        {
            virtualPath = relativePath;
            title = Path.GetFileNameWithoutExtension(relativePath);
            rejectionReason = "invalid_path";
        }

        byte[]? bytes = null;
        if (declaredSizeBytes <= MaxDocumentSourceBytes)
        {
            try
            {
                bytes = await ReadBoundedAsync(
                    path,
                    MaxDocumentSourceBytes,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                rejectionReason ??= "read_failed";
            }
        }

        string sha256;
        if (bytes is not null)
        {
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (rejectionReason is null
                && !TryDecodeUtf8(bytes, out _))
            {
                rejectionReason = "invalid_utf8";
            }
        }
        else
        {
            try
            {
                sha256 = await HashFileAsync(path, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                sha256 = string.Empty;
                rejectionReason ??= "read_failed";
            }

            if (rejectionReason is null)
            {
                rejectionReason = "document_too_large";
            }
        }

        return new ImportSourceFile(
            relativePath,
            virtualPath,
            title,
            format,
            bytes?.LongLength ?? declaredSizeBytes,
            sha256,
            rejectionReason);
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream(
            capacity: (int)Math.Min(stream.Length, maxBytes));
        var chunk = new byte[81_920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool TryDecodeUtf8(byte[] bytes, out string content)
    {
        try
        {
            var span = bytes.AsSpan();
            if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                span = span[3..];
            }

            content = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            content = string.Empty;
            return false;
        }
    }

    private static void EnsureNoReparsePoints(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidSource("Import source path contains a link.");
            }
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return string.Equals(fullPath, root, comparison)
            || fullPath.StartsWith(prefix, comparison);
    }

    private static string ToVirtualRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static VaultDocumentFormat? FormatFromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" => VaultDocumentFormat.Markdown,
            ".txt" => VaultDocumentFormat.Text,
            _ => null
        };

    private static VaultException InvalidSource(string message) =>
        new(VaultErrorCode.ImportSourceInvalid, message);

    private static VaultException SourceLimitExceeded(
        string limit,
        long maximum) =>
        new(
            VaultErrorCode.ImportSourceInvalid,
            "Import source exceeds the bounded scan limit.",
            details: new Dictionary<string, object?>
            {
                ["limit"] = limit,
                ["maximum"] = maximum
            });
}
