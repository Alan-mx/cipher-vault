using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal sealed record NormalizedTag(
    string Name,
    string NormalizedKey);

internal static class TagNormalizer
{
    internal const int MaxTags = 50;
    internal const int MaxScalars = 64;

    internal static NormalizedTag Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidTag("Tag must not be empty.");
        }

        var name = UnicodeText.CollapseWhitespace(value);
        int scalarCount;
        try
        {
            scalarCount = UnicodeText.CountScalars(name);
        }
        catch (ArgumentException)
        {
            throw InvalidTag("Tag contains invalid Unicode data.");
        }

        if (scalarCount is < 1 or > MaxScalars)
        {
            throw InvalidTag(
                $"Each tag must contain 1 to {MaxScalars} Unicode characters.");
        }

        return new NormalizedTag(name, name.ToUpperInvariant());
    }

    internal static IReadOnlyList<NormalizedTag> NormalizeMany(
        IEnumerable<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var result = new List<NormalizedTag>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (result.Count >= MaxTags)
            {
                throw new VaultException(
                    VaultErrorCode.TagLimitExceeded,
                    $"A document may have at most {MaxTags} tags.");
            }

            if (keys.Add(normalized.NormalizedKey))
            {
                result.Add(normalized);
            }
        }

        return result
            .OrderBy(tag => tag.NormalizedKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static VaultException InvalidTag(string message) =>
        new(VaultErrorCode.InvalidInput, message);
}
