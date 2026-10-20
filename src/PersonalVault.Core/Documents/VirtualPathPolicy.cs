using System.Text;
using PersonalVault.Core.Errors;

namespace PersonalVault.Core.Documents;

internal static class VirtualPathPolicy
{
    private const string InvalidFileCharacters = "<>:\"/\\|?*";

    public static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        var normalized = folder
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/')
            .Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }
        foreach (var segment in segments)
        {
            ValidateSegment(segment);
        }
        return string.Join('/', segments);
    }

    public static (string SourcePath, string SourcePathNorm) BuildDocumentPath(
        string title,
        string? folder,
        string format)
    {
        var normalizedFolder = NormalizeFolder(folder);
        var fileName = SanitizeFileName(title);
        var extension = format switch
        {
            "md" => ".md",
            "txt" => ".txt",
            _ => throw new VaultException(VaultErrorCode.InvalidInput, "Only md and txt are supported.")
        };
        var sourcePath = normalizedFolder.Length == 0
            ? fileName + extension
            : $"{normalizedFolder}/{fileName}{extension}";
        return (
            sourcePath,
            sourcePath.Normalize(NormalizationForm.FormC).ToLowerInvariant());
    }

    public static (string SourcePath, string SourcePathNorm) NormalizeDocumentPath(
        string path,
        string format)
    {
        var normalized = path
            .Normalize(NormalizationForm.FormC)
            .Replace('\\', '/')
            .Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Document path is invalid.");
        }
        foreach (var segment in segments)
        {
            ValidateSegment(segment);
        }

        var expectedExtension = format switch
        {
            "md" => ".md",
            "txt" => ".txt",
            _ => throw new VaultException(VaultErrorCode.InvalidInput, "Unsupported document format.")
        };
        if (!normalized.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Document path must end with {expectedExtension}.");
        }
        return (normalized, normalized.ToLowerInvariant());
    }

    public static string NormalizeTag(string value)
    {
        var normalized = string.Join(
            ' ',
            value
                .Normalize(NormalizationForm.FormC)
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is < 1 or > 64)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Each tag must contain 1 to 64 characters.");
        }
        return normalized;
    }

    private static string SanitizeFileName(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var character in title.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(character) || InvalidFileCharacters.Contains(character))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(character);
            }
        }

        var sanitized = builder
            .ToString()
            .Trim()
            .TrimEnd('.');
        if (sanitized.Length == 0)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Title cannot be converted to a valid filename.");
        }
        return sanitized;
    }

    private static void ValidateSegment(string segment)
    {
        if (segment is "." or ".." ||
            segment.IndexOfAny(InvalidFileCharacters.ToCharArray()) >= 0 ||
            segment.Any(char.IsControl))
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Folder path is invalid.");
        }
    }
}
