namespace CipherVault.Core.RelatedKnowledge;

internal static class RelatedKnowledgeScorer
{
    internal const int SameProjectScore = 40;
    internal const int SameTypeScore = 20;
    internal const int TagScore = 8;
    internal const int MaxTagScore = 24;
    internal const int PropertyScore = 4;
    internal const int MaxPropertyScore = 12;
    internal const int TwoHopScore = 15;
    internal const int MaxTitleScore = 20;
    internal const double MinimumScore = 8;

    internal static RelatedSignalSet BuildSignals(
        RelatedKnowledgeDocument source,
        RelatedKnowledgeDocument candidate,
        bool twoHop)
    {
        var sourceProfile = RelatedFrontMatterSignals.Read(source.FrontMatter);
        var candidateProfile = RelatedFrontMatterSignals.Read(candidate.FrontMatter);
        var sourceTags = source.Tags.ToDictionary(
            tag => tag.NormalizedKey,
            StringComparer.Ordinal);
        var sharedTags = candidate.Tags
            .Where(tag => sourceTags.ContainsKey(tag.NormalizedKey))
            .OrderBy(tag => tag.NormalizedKey, StringComparer.Ordinal)
            .Select(tag => tag.Name)
            .ToArray();
        var sourceProperties = sourceProfile.Properties.ToDictionary(
            property => property.Key,
            StringComparer.Ordinal);
        var sharedProperties = candidateProfile.Properties
            .Where(property =>
                sourceProperties.TryGetValue(property.Key, out var sourceProperty)
                && RelatedFrontMatterSignals.ValuesEqual(
                    sourceProperty,
                    property))
            .Select(property => property.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        return new RelatedSignalSet(
            IsSame(
                sourceProfile.Project,
                candidateProfile.Project,
                StringComparison.OrdinalIgnoreCase),
            IsSame(
                sourceProfile.Type,
                candidateProfile.Type,
                StringComparison.OrdinalIgnoreCase),
            sharedTags,
            sharedProperties,
            twoHop,
            RelatedTitleSimilarity.Calculate(
                source.Title,
                candidate.Title));
    }

    internal static RelatedScoredCandidate Score(
        RelatedKnowledgeDocument candidate,
        RelatedSignalSet signals)
    {
        var reasons = new List<RelatedReasonContribution>();
        if (signals.SameProject)
        {
            reasons.Add(new RelatedReasonContribution(
                RelatedReasonCodes.SameProject,
                "同一项目",
                SameProjectScore));
        }
        if (signals.SameType)
        {
            reasons.Add(new RelatedReasonContribution(
                RelatedReasonCodes.SameType,
                "同一类型",
                SameTypeScore));
        }
        if (signals.SharedTagNames.Count > 0)
        {
            reasons.Add(new RelatedReasonContribution(
                RelatedReasonCodes.SharedTags,
                FormatSharedLabel("共享标签", signals.SharedTagNames),
                Math.Min(
                    signals.SharedTagNames.Count * TagScore,
                    MaxTagScore)));
        }
        if (signals.SharedPropertyNames.Count > 0)
        {
            reasons.Add(new RelatedReasonContribution(
                RelatedReasonCodes.SharedProperties,
                FormatSharedLabel(
                    "共享属性",
                    signals.SharedPropertyNames),
                Math.Min(
                    signals.SharedPropertyNames.Count * PropertyScore,
                    MaxPropertyScore)));
        }
        if (signals.TwoHop)
        {
            reasons.Add(new RelatedReasonContribution(
                RelatedReasonCodes.TwoHopLink,
                "通过关联文档连接",
                TwoHopScore));
        }

        var titleContribution = Math.Round(
            Math.Clamp(
                signals.TitleSimilarity * MaxTitleScore,
                0,
                MaxTitleScore),
            2,
            MidpointRounding.AwayFromZero);
        if (titleContribution > 0)
        {
            reasons.Add(new RelatedReasonContribution(
                RelatedReasonCodes.SimilarTitle,
                "标题相似",
                titleContribution));
        }

        var orderedReasons = reasons
            .OrderByDescending(reason => reason.Contribution)
            .ThenBy(reason => reason.Code, StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var score = Math.Round(
            reasons.Sum(reason => reason.Contribution),
            2,
            MidpointRounding.AwayFromZero);
        return new RelatedScoredCandidate(
            candidate,
            score,
            orderedReasons);
    }

    private static bool IsSame(
        string? left,
        string? right,
        StringComparison comparison) =>
        left is not null
        && right is not null
        && string.Equals(left, right, comparison);

    private static string FormatSharedLabel(
        string prefix,
        IReadOnlyList<string> values)
    {
        var visible = string.Join("、", values.Take(3));
        return values.Count > 3
            ? $"{prefix}：{visible} 等"
            : $"{prefix}：{visible}";
    }
}

internal static class RelatedReasonCodes
{
    internal const string SameProject = "same_project";
    internal const string SameType = "same_type";
    internal const string SharedTags = "shared_tags";
    internal const string SharedProperties = "shared_properties";
    internal const string TwoHopLink = "two_hop_link";
    internal const string SimilarTitle = "similar_title";
}
