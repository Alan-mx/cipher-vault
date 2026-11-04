namespace CipherVault.Core.Documents;

internal static class MarkdownLinkWriter
{
    internal static string BuildRelativeDestination(
        string sourceVirtualPath,
        string targetVirtualPath,
        string? targetHeading)
    {
        var sourceSegments = sourceVirtualPath.Split('/');
        var targetSegments = targetVirtualPath.Split('/');
        var sourceDirectoryLength = sourceSegments.Length - 1;
        var sharedPrefixLength = 0;
        while (sharedPrefixLength < sourceDirectoryLength
               && sharedPrefixLength < targetSegments.Length - 1
               && string.Equals(
                   sourceSegments[sharedPrefixLength],
                   targetSegments[sharedPrefixLength],
                   StringComparison.Ordinal))
        {
            sharedPrefixLength++;
        }

        var relativeSegments = new List<string>();
        for (var index = sharedPrefixLength;
             index < sourceDirectoryLength;
             index++)
        {
            relativeSegments.Add("..");
        }
        for (var index = sharedPrefixLength; index < targetSegments.Length; index++)
        {
            relativeSegments.Add(targetSegments[index]);
        }

        var path = string.Join(
            '/',
            relativeSegments.Select(EncodePathSegment));
        if (targetHeading is null)
        {
            return path;
        }

        return $"{path}#{Uri.EscapeDataString(targetHeading)}";
    }

    internal static string ReplaceDestination(
        string content,
        int destinationStart,
        int destinationEnd,
        string destination)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(destination);
        if (destinationStart < 0
            || destinationEnd <= destinationStart
            || destinationEnd > content.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationStart),
                "Markdown destination offsets are invalid.");
        }

        return string.Concat(
            content.AsSpan(0, destinationStart),
            destination,
            content.AsSpan(destinationEnd));
    }

    private static string EncodePathSegment(string segment) =>
        Uri.EscapeDataString(segment)
            .Replace("%2E", ".", StringComparison.OrdinalIgnoreCase);
}
