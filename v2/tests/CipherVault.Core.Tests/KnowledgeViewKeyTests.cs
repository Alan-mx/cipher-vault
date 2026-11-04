using CipherVault.Core.Knowledge;

namespace CipherVault.Core.Tests;

public sealed class KnowledgeViewKeyTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Project A  ", "Project A")]
    [InlineData("ＡＢＣ", "ＡＢＣ")]
    public void LabelNormalizesNfcAndTrim(string? value, string? expected)
    {
        Assert.Equal(expected, KnowledgeViewKey.NormalizeLabel(value));
    }

    [Theory]
    [InlineData("project a", "PROJECT A")]
    [InlineData("  Project A  ", "PROJECT A")]
    [InlineData("Ä", "Ä")]
    [InlineData("ς", "Σ")]
    public void KeyUsesInvariantCaseNormalization(
        string value,
        string expected)
    {
        Assert.Equal(expected, KnowledgeViewKey.NormalizeKey(value));
    }

    [Fact]
    public void OrdinalSortKeyMatchesDotNetOrdinalComparer()
    {
        var values = new[]
        {
            "A",
            "a",
            "Ä",
            "中",
            "😀",
            "𐐀"
        };
        foreach (var left in values)
        {
            foreach (var right in values)
            {
                Assert.Equal(
                    Math.Sign(StringComparer.Ordinal.Compare(left, right)),
                    Math.Sign(
                        KnowledgeViewKey.OrdinalSortKey(left)
                            .AsSpan()
                            .SequenceCompareTo(
                                KnowledgeViewKey.OrdinalSortKey(right))));
            }
        }
    }
}
