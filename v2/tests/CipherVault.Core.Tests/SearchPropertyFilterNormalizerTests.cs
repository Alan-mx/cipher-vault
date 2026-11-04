using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.Search;

namespace CipherVault.Core.Tests;

public sealed class SearchPropertyFilterNormalizerTests
{
    [Fact]
    public void CanonicalKeyIsStableAcrossOrderAndInDuplicates()
    {
        var first = new[]
        {
            Filter("status", SearchPropertyOperator.Eq, "\"verified\""),
            Filter(
                "maturity",
                SearchPropertyOperator.In,
                "[\"practiced\", \"verified\", \"practiced\"]")
        };
        var reordered = new[]
        {
            Filter(
                "maturity",
                SearchPropertyOperator.In,
                "[\"verified\", \"practiced\"]"),
            Filter("status", SearchPropertyOperator.Eq, "\"verified\"")
        };

        var firstKey = SearchPropertyFilterNormalizer.BuildFingerprintKey(
            SearchPropertyFilterNormalizer.Normalize(first),
            SearchPropertyMatch.All);
        var secondKey = SearchPropertyFilterNormalizer.BuildFingerprintKey(
            SearchPropertyFilterNormalizer.Normalize(reordered),
            SearchPropertyMatch.All);

        Assert.Equal(firstKey, secondKey);
    }

    [Theory]
    [InlineData("tags")]
    [InlineData("tags.name")]
    [InlineData("source..kind")]
    [InlineData("source[0]")]
    [InlineData("source.*")]
    [InlineData("source/kind")]
    public void InvalidPathsAreRejected(string field)
    {
        var exception = Assert.Throws<VaultException>(() =>
            SearchPropertyFilterNormalizer.Normalize(
                [Filter(field, SearchPropertyOperator.Exists, "true")]));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void FilterCountAndOperatorValueTypesAreBounded()
    {
        var tooMany = Enumerable.Range(0, 17)
            .Select(index => Filter(
                $"field{index}",
                SearchPropertyOperator.Exists,
                "true"))
            .ToArray();
        AssertInvalid(() =>
            SearchPropertyFilterNormalizer.Normalize(tooMany));

        AssertInvalid(() =>
            SearchPropertyFilterNormalizer.Normalize(
                [Filter("status", SearchPropertyOperator.Exists, "\"yes\"")]));
        AssertInvalid(() =>
            SearchPropertyFilterNormalizer.Normalize(
                [Filter("status", SearchPropertyOperator.Contains, "[\"x\"]")]));
        AssertInvalid(() =>
            SearchPropertyFilterNormalizer.Normalize(
                [Filter("status", SearchPropertyOperator.Gte, "{}")]));
    }

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<VaultException>(action);
        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private static SearchPropertyFilter Filter(
        string field,
        SearchPropertyOperator operation,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return new SearchPropertyFilter(
            field,
            operation,
            document.RootElement.Clone());
    }
}
