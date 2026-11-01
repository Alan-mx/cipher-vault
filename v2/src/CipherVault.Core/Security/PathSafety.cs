using CipherVault.Core.Errors;

namespace CipherVault.Core.Security;

public sealed record PathRelation(
    bool IsEqual,
    bool IsChildOf,
    bool IsAncestorOf);

public static class PathSafety
{
    public static string Canonicalize(
        string path,
        string fieldName,
        bool requireAbsolute = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw InvalidPath($"{fieldName} is required.");
        }

        try
        {
            if (requireAbsolute && !Path.IsPathFullyQualified(path))
            {
                throw InvalidPath($"{fieldName} must be an absolute path.");
            }

            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw InvalidPath($"{fieldName} is invalid.");
        }
    }

    public static PathRelation GetRelation(
        string left,
        string right)
    {
        var normalizedLeft = Canonicalize(left, "left path");
        var normalizedRight = Canonicalize(right, "right path");
        if (string.Equals(
                normalizedLeft,
                normalizedRight,
                PathComparison))
        {
            return new PathRelation(
                IsEqual: true,
                IsChildOf: false,
                IsAncestorOf: false);
        }

        return new PathRelation(
            IsEqual: false,
            IsChildOf: IsDescendant(normalizedLeft, normalizedRight),
            IsAncestorOf: IsDescendant(normalizedRight, normalizedLeft));
    }

    public static void EnsureNoLinkComponents(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var current = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));

        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw InvalidPath(
                        "Path must not contain symbolic links or reparse points.");
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or DirectoryNotFoundException)
            {
                // Missing components are allowed only when the caller
                // intentionally validates a future target path.
            }
            catch (VaultException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to inspect a path component.",
                    exception);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, current, PathComparison))
            {
                break;
            }

            current = parent;
        }
    }

    public static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0
                && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsDescendant(
        string candidate,
        string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "."
            && !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                PathComparison)
            && !relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                PathComparison);
    }

    private static VaultException InvalidPath(string message) =>
        new(VaultErrorCode.InvalidInput, message);
}
