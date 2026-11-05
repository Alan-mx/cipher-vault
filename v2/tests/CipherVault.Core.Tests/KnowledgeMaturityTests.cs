using CipherVault.Core.Knowledge;

namespace CipherVault.Core.Tests;

public sealed class KnowledgeMaturityTests
{
    [Theory]
    [InlineData(null, null, KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("null", null, KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("integer", "7", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("true", "true", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("array", "[]", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("object", "{}", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("text", "", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("text", "   ", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("text", " RAW ", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("text", "Excerpt", KnowledgeMaturityQueueBucket.PendingOrganization)]
    [InlineData("text", "organized", KnowledgeMaturityQueueBucket.PendingVerification)]
    [InlineData("text", " VERIFIED ", KnowledgeMaturityQueueBucket.Completed)]
    [InlineData("text", "practiced", KnowledgeMaturityQueueBucket.Completed)]
    [InlineData("text", "reviewed", KnowledgeMaturityQueueBucket.Other)]
    public void ClassifyCoversAllMaturityShapes(
        string? jsonType,
        string? value,
        KnowledgeMaturityQueueBucket expected)
    {
        Assert.Equal(expected, KnowledgeMaturity.Classify(jsonType, value));
    }

    [Theory]
    [InlineData(null, null, KnowledgeMaturityLevel.Raw)]
    [InlineData("null", null, KnowledgeMaturityLevel.Raw)]
    [InlineData("integer", "7", KnowledgeMaturityLevel.Raw)]
    [InlineData("text", "", KnowledgeMaturityLevel.Raw)]
    [InlineData("text", " raw ", KnowledgeMaturityLevel.Excerpt)]
    [InlineData("text", "excerpt", KnowledgeMaturityLevel.Organized)]
    [InlineData("text", "organized", KnowledgeMaturityLevel.Verified)]
    [InlineData("text", "verified", KnowledgeMaturityLevel.Practiced)]
    [InlineData("text", "practiced", null)]
    [InlineData("text", "reviewed", null)]
    public void NextMaturityFollowsFixedChain(
        string? jsonType,
        string? value,
        KnowledgeMaturityLevel? expected)
    {
        Assert.Equal(expected, KnowledgeMaturity.Next(jsonType, value));
    }

    [Fact]
    public void ValueTypeMapsJsonTypesWithoutReturningValues()
    {
        Assert.Equal(
            KnowledgeMaturityValueType.Missing,
            KnowledgeMaturity.ParseValueType(null));
        Assert.Equal(
            KnowledgeMaturityValueType.Null,
            KnowledgeMaturity.ParseValueType("null"));
        Assert.Equal(
            KnowledgeMaturityValueType.Number,
            KnowledgeMaturity.ParseValueType("integer"));
        Assert.Equal(
            KnowledgeMaturityValueType.Number,
            KnowledgeMaturity.ParseValueType("real"));
        Assert.Equal(
            KnowledgeMaturityValueType.Boolean,
            KnowledgeMaturity.ParseValueType("true"));
        Assert.Equal(
            KnowledgeMaturityValueType.Boolean,
            KnowledgeMaturity.ParseValueType("false"));
        Assert.Equal(
            KnowledgeMaturityValueType.Array,
            KnowledgeMaturity.ParseValueType("array"));
        Assert.Equal(
            KnowledgeMaturityValueType.Object,
            KnowledgeMaturity.ParseValueType("object"));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData(" RAW ")]
    [InlineData("Excerpt")]
    [InlineData("organized")]
    [InlineData("VERIFIED")]
    [InlineData("practiced")]
    public void StandardValuesAcceptCaseAndWhitespace(string value)
    {
        Assert.True(KnowledgeMaturity.TryParseStandard(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("reviewed")]
    public void NonStandardValuesAreRejected(string? value)
    {
        Assert.False(KnowledgeMaturity.TryParseStandard(value, out _));
    }
}
