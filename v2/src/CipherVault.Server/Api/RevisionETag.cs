using System.Globalization;
using System.Text.RegularExpressions;

namespace CipherVault.Server.Api;

public sealed record RevisionETag(int Revision)
{
    private static readonly Regex Pattern = new(
        "^\"(?<revision>[1-9][0-9]*)\"$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string ToHeaderValue() =>
        $"\"{Revision.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParse(string? value, out RevisionETag? etag)
    {
        etag = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Pattern.Match(value);
        if (!match.Success
            || !int.TryParse(
                match.Groups["revision"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var revision)
            || revision < 1)
        {
            return false;
        }

        etag = new RevisionETag(revision);
        return true;
    }
}
