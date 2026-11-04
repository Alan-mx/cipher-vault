using System.Text;

namespace CipherVault.Core.Documents;

internal sealed record MarkdownLinkTarget(
    string RawTarget,
    string CanonicalTargetKey,
    string? TargetHeading,
    string? TargetHeadingKey);

internal static class MarkdownLinkPathResolver
{
    private const int MaxRawTargetLength = 4096;
    private const int MaxHeadingLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static MarkdownLinkTarget? Resolve(
        string rawDestination,
        NormalizedVirtualPath sourcePath)
    {
        if (string.IsNullOrWhiteSpace(rawDestination)
            || rawDestination.Length > MaxRawTargetLength
            || rawDestination.Contains('\\', StringComparison.Ordinal)
            || rawDestination.Contains('\r', StringComparison.Ordinal)
            || rawDestination.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        var fragmentSeparator = rawDestination.IndexOf('#', StringComparison.Ordinal);
        var rawPath = fragmentSeparator >= 0
            ? rawDestination[..fragmentSeparator]
            : rawDestination;
        var rawFragment = fragmentSeparator >= 0
            ? rawDestination[(fragmentSeparator + 1)..]
            : null;
        if (rawPath.Length == 0 || IsExternalDestination(rawPath))
        {
            return null;
        }

        var decodedPath = PercentDecode(rawPath);
        if (decodedPath is null
            || decodedPath.Length == 0
            || decodedPath.Contains('?', StringComparison.Ordinal)
            || decodedPath.Contains('\\', StringComparison.Ordinal)
            || IsExternalDestination(decodedPath))
        {
            return null;
        }

        var isRooted = decodedPath[0] == '/';
        var pathBody = isRooted ? decodedPath[1..] : decodedPath;
        if (pathBody.Length == 0
            || pathBody.Contains("//", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = new List<string>();
        if (!isRooted)
        {
            segments.AddRange(
                sourcePath.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .SkipLast(1));
        }

        foreach (var segment in pathBody.Split('/'))
        {
            if (segment.Length == 0)
            {
                return null;
            }
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return null;
        }
        var extension = Path.GetExtension(segments[^1]);
        var format = extension.ToLowerInvariant() switch
        {
            ".md" => VaultDocumentFormat.Markdown,
            ".txt" => VaultDocumentFormat.Text,
            _ => (VaultDocumentFormat?)null
        };
        if (format is null)
        {
            return null;
        }

        segments[^1] = segments[^1][..^extension.Length]
            + extension.ToLowerInvariant();
        NormalizedVirtualPath canonicalPath;
        try
        {
            canonicalPath = VirtualPath.Normalize(
                string.Join('/', segments),
                format.Value);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or CipherVault.Core.Errors.VaultException)
        {
            return null;
        }

        string? targetHeading = null;
        string? targetHeadingKey = null;
        if (rawFragment is not null)
        {
            targetHeading = PercentDecode(rawFragment);
            if (targetHeading is null)
            {
                return null;
            }
            targetHeading = targetHeading.Normalize(NormalizationForm.FormC);
            if (targetHeading.Length == 0)
            {
                targetHeading = null;
            }
            else
            {
                if (targetHeading.Length > MaxHeadingLength)
                {
                    return null;
                }
                targetHeadingKey = NormalizeHeadingKey(targetHeading);
                if (targetHeadingKey.Length == 0
                    || targetHeadingKey.Length > MaxHeadingLength)
                {
                    return null;
                }
            }
        }

        return new MarkdownLinkTarget(
            rawDestination.Normalize(NormalizationForm.FormC),
            canonicalPath.PathKey,
            targetHeading,
            targetHeadingKey);
    }

    internal static string NormalizeHeadingKey(string heading) =>
        UnicodeText.CollapseWhitespace(heading)
            .ToUpperInvariant();

    private static bool IsExternalDestination(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }
        for (var index = 1; index < colon; index++)
        {
            var character = value[index];
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }
        return true;
    }

    private static string? PercentDecode(string value)
    {
        try
        {
            var bytes = new List<byte>(value.Length);
            Span<byte> encoded = stackalloc byte[4];
            for (var index = 0; index < value.Length;)
            {
                if (value[index] == '%')
                {
                    if (index + 2 >= value.Length
                        || !TryParseHex(value[index + 1], out var high)
                        || !TryParseHex(value[index + 2], out var low))
                    {
                        return null;
                    }
                    bytes.Add((byte)((high << 4) | low));
                    index += 3;
                    continue;
                }

                var rune = Rune.GetRuneAt(value, index);
                var byteCount = rune.EncodeToUtf8(encoded);
                bytes.AddRange(encoded[..byteCount].ToArray());
                index += rune.Utf16SequenceLength;
            }
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException
            or ArgumentException)
        {
            return null;
        }
    }

    private static bool TryParseHex(char character, out int value)
    {
        value = character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1
        };
        return value >= 0;
    }
}
