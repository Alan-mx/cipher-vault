using System.Text;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal sealed record NormalizedVirtualPath(
    string Path,
    string PathKey,
    string FolderKey,
    VaultDocumentFormat Format);

internal static class VirtualPath
{
    internal const int MaxLength = 2048;
    private const string InvalidFileCharacters = "<>:\"/\\|?*";

    internal static NormalizedVirtualPath Normalize(
        string path,
        VaultDocumentFormat format)
    {
        if (string.IsNullOrEmpty(path)
            || path.Length > MaxLength
            || path[0] == '/'
            || path[^1] == '/')
        {
            throw InvalidPath();
        }

        var normalized = path.Normalize(NormalizationForm.FormC);
        if (normalized.IndexOf('\\') >= 0)
        {
            throw InvalidPath();
        }

        var segments = normalized.Split('/');
        if (segments.Length == 0
            || segments.Any(segment => !IsValidSegment(segment)))
        {
            throw InvalidPath();
        }

        var expectedExtension = format.ToExtension();
        if (!normalized.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Document path must end with {expectedExtension}.");
        }

        var folder = segments.Length == 1
            ? string.Empty
            : string.Join('/', segments[..^1]);
        return new NormalizedVirtualPath(
            normalized,
            normalized,
            folder,
            format);
    }

    internal static NormalizedVirtualPath Build(
        string title,
        string? folder,
        VaultDocumentFormat format)
    {
        var normalizedTitle = TitleNormalizer.NormalizeTitle(title);
        var normalizedFolder = NormalizeFolder(folder);
        var filename = SanitizeFileName(normalizedTitle) + format.ToExtension();
        var path = normalizedFolder.Length == 0
            ? filename
            : $"{normalizedFolder}/{filename}";
        return Normalize(path, format);
    }

    internal static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        if (folder.Length > MaxLength
            || folder[0] == '/'
            || folder[^1] == '/'
            || folder.Contains('\\'))
        {
            throw InvalidPath();
        }

        var normalized = folder.Normalize(NormalizationForm.FormC);
        var segments = normalized.Split('/');
        if (segments.Any(segment => !IsValidSegment(segment)))
        {
            throw InvalidPath();
        }

        return normalized;
    }

    internal static string ToLinkPathKey(string path, VaultDocumentFormat format)
    {
        var normalized = Normalize(path, format).Path;
        return normalized[..^format.ToExtension().Length].ToUpperInvariant();
    }

    private static string SanitizeFileName(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var character in title.Normalize(NormalizationForm.FormC))
        {
            builder.Append(
                char.IsControl(character)
                || InvalidFileCharacters.Contains(character)
                || character == '/'
                    ? '_'
                    : character);
        }

        var sanitized = builder.ToString().Trim().TrimEnd(' ', '.');
        if (sanitized.Length == 0)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Title cannot be converted to a valid document filename.");
        }

        return sanitized;
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0
            || segment is "." or ".."
            || segment.IndexOfAny(InvalidFileCharacters.ToCharArray()) >= 0
            || segment.Any(char.IsControl)
            || segment[^1] is ' ' or '.'
            || IsReservedDeviceName(segment))
        {
            return false;
        }

        return true;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && char.IsAsciiDigit(stem[3])
            && stem[3] != '0';
    }

    private static VaultException InvalidPath() =>
        new(
            VaultErrorCode.InvalidInput,
            "Document virtual path is invalid.");
}
