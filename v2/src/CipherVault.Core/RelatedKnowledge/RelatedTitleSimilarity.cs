using CipherVault.Core.Documents;

namespace CipherVault.Core.RelatedKnowledge;

internal static class RelatedTitleSimilarity
{
    internal static double Calculate(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal))
        {
            return 1;
        }

        var leftTrigrams = CreateTrigrams(normalizedLeft);
        var rightTrigrams = CreateTrigrams(normalizedRight);
        if (leftTrigrams.Count == 0
            || rightTrigrams.Count == 0
            || leftTrigrams.Values.Sum() == 0
            || rightTrigrams.Values.Sum() == 0)
        {
            return 0;
        }

        var overlap = leftTrigrams.Sum(pair =>
            Math.Min(pair.Value, rightTrigrams.GetValueOrDefault(pair.Key)));
        return 2d * overlap
            / (leftTrigrams.Values.Sum() + rightTrigrams.Values.Sum());
    }

    internal static string Normalize(string title) =>
        UnicodeText.CollapseWhitespace(title).ToUpperInvariant();

    internal static string NormalizeForSearch(string title) =>
        UnicodeText.CollapseWhitespace(title);

    private static Dictionary<(int First, int Second, int Third), int>
        CreateTrigrams(string value)
    {
        var scalars = value
            .EnumerateRunes()
            .Select(rune => rune.Value)
            .ToArray();
        var trigrams =
            new Dictionary<(int First, int Second, int Third), int>();
        for (var index = 0; index <= scalars.Length - 3; index++)
        {
            var key = (
                scalars[index],
                scalars[index + 1],
                scalars[index + 2]);
            trigrams[key] = trigrams.GetValueOrDefault(key) + 1;
        }
        return trigrams;
    }
}
