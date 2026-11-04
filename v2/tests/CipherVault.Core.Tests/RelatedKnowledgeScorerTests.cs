using System.Text.Json;
using CipherVault.Core.RelatedKnowledge;

namespace CipherVault.Core.Tests;

public sealed class RelatedKnowledgeScorerTests
{
    [Fact]
    public void BuildSignalsAndScoreUseComparableMetadata()
    {
        var source = Document(
            "Redis Cache Strategy",
            """
            {
              "project": "Cipher Vault",
              "type": "note",
              "status": "verified",
              "maturity": 3,
              "enabled": true
            }
            """,
            ("Architecture", "ARCHITECTURE"),
            ("Redis", "REDIS"));
        var candidate = Document(
            "Redis Cache Strategy",
            """
            {
              "project": "cipher vault",
              "type": "NOTE",
              "status": "verified",
              "maturity": 3,
              "enabled": true
            }
            """,
            ("architecture", "ARCHITECTURE"),
            ("database", "DATABASE"));

        var signals = RelatedKnowledgeScorer.BuildSignals(
            source,
            candidate,
            twoHop: true);
        var scored = RelatedKnowledgeScorer.Score(candidate, signals);

        Assert.True(signals.SameProject);
        Assert.True(signals.SameType);
        Assert.Equal(["architecture"], signals.SharedTagNames);
        Assert.Equal(
            ["enabled", "maturity", "status"],
            signals.SharedPropertyNames);
        Assert.Equal(1, signals.TitleSimilarity);
        Assert.Equal(115, scored.Score);
        Assert.Equal(
            [
                RelatedReasonCodes.SameProject,
                RelatedReasonCodes.SameType,
                RelatedReasonCodes.SimilarTitle
            ],
            scored.Reasons.Select(reason => reason.Code));
    }

    [Fact]
    public void ReservedAndNonScalarPropertiesDoNotCreateSignals()
    {
        var source = Document(
            "Source",
            """
            {
              "id": "same",
              "tags": ["one"],
              "created": "same",
              "updated": "same",
              "project": "Same Project",
              "type": "same",
              "nested": { "value": 1 },
              "items": [1, 2],
              "empty": null
            }
            """);
        var candidate = Document(
            "Other",
            """
            {
              "id": "same",
              "tags": ["one"],
              "created": "same",
              "updated": "same",
              "Project": "Same Project",
              "TYPE": "same",
              "nested": { "value": 1 },
              "items": [1, 2],
              "empty": null
            }
            """);

        var signals = RelatedKnowledgeScorer.BuildSignals(
            source,
            candidate,
            twoHop: false);

        Assert.False(signals.SameProject);
        Assert.False(signals.SameType);
        Assert.Empty(signals.SharedPropertyNames);
        Assert.False(signals.HasNonTitleSignal);
    }

    [Theory]
    [InlineData("AI", "ai", 1)]
    [InlineData("Redis Cache", "redis cache", 1)]
    [InlineData("😀資料庫", "😀資料庫", 1)]
    [InlineData("Redis Cache", "Quantum", 0)]
    [InlineData("AB", "CD", 0)]
    public void TitleSimilarityUsesNormalizedUnicodeScalarTrigrams(
        string left,
        string right,
        double expected)
    {
        Assert.Equal(
            expected,
            RelatedTitleSimilarity.Calculate(left, right),
            precision: 6);
    }

    [Fact]
    public void SharedPropertyScoringIsCappedAtThreeFields()
    {
        var source = Document(
            "Source",
            """
            {
              "a": 1,
              "b": 2,
              "c": 3,
              "d": 4
            }
            """);
        var candidate = Document(
            "Candidate",
            """
            {
              "a": 1,
              "b": 2,
              "c": 3,
              "d": 4
            }
            """);

        var signals = RelatedKnowledgeScorer.BuildSignals(
            source,
            candidate,
            twoHop: false);
        var scored = RelatedKnowledgeScorer.Score(candidate, signals);

        Assert.Equal(["a", "b", "c", "d"], signals.SharedPropertyNames);
        Assert.Equal(12, scored.Score);
        Assert.Equal(
            "共享属性：a、b、c 等",
            Assert.Single(scored.Reasons).Label);
    }

    private static RelatedKnowledgeDocument Document(
        string title,
        string frontMatter,
        params (string Name, string Key)[] tags)
    {
        using var json = JsonDocument.Parse(frontMatter);
        return new RelatedKnowledgeDocument(
            Guid.NewGuid(),
            title,
            $"{title}.md",
            1,
            json.RootElement.Clone(),
            tags.Select(tag => new RelatedTag(tag.Name, tag.Key)).ToArray(),
            DateTimeOffset.Parse("2026-11-04T00:00:00Z"));
    }
}
