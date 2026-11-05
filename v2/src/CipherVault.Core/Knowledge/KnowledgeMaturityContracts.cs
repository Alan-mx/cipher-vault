using CipherVault.Core.Documents;
using System.Text.Json.Serialization;

namespace CipherVault.Core.Knowledge;

public enum KnowledgeMaturityQueueBucket
{
    PendingOrganization,
    PendingVerification,
    Completed,
    Other
}

public enum KnowledgeMaturityValueType
{
    Missing,
    Null,
    String,
    Number,
    Boolean,
    Array,
    Object
}

public enum KnowledgeMaturityLevel
{
    Raw,
    Excerpt,
    Organized,
    Verified,
    Practiced
}

public sealed record KnowledgeMaturityQueueItem(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    VaultDocumentFormat Format,
    int Revision,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? MaturityValue,
    KnowledgeMaturityValueType MaturityValueType,
    KnowledgeMaturityQueueBucket MaturityBucket,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    KnowledgeMaturityLevel? NextMaturity);

public sealed record KnowledgeMaturityQueueQuery(
    KnowledgeMaturityQueueBucket Bucket,
    CursorPageRequest Page);

public sealed record KnowledgeMaturityQueuePage(
    IReadOnlyList<KnowledgeMaturityQueueItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? NextCursor);

internal static class KnowledgeMaturity
{
    internal static KnowledgeMaturityQueueBucket Classify(
        string? jsonType,
        string? rawValue)
    {
        if (!string.Equals(jsonType, "text", StringComparison.Ordinal)
            || !TryParseStandard(rawValue, out var level))
        {
            return string.Equals(jsonType, "text", StringComparison.Ordinal)
                && KnowledgeViewKey.NormalizeLabel(rawValue) is not null
                ? KnowledgeMaturityQueueBucket.Other
                : KnowledgeMaturityQueueBucket.PendingOrganization;
        }

        return level switch
        {
            KnowledgeMaturityLevel.Raw or
                KnowledgeMaturityLevel.Excerpt =>
                KnowledgeMaturityQueueBucket.PendingOrganization,
            KnowledgeMaturityLevel.Organized =>
                KnowledgeMaturityQueueBucket.PendingVerification,
            KnowledgeMaturityLevel.Verified or
                KnowledgeMaturityLevel.Practiced =>
                KnowledgeMaturityQueueBucket.Completed,
            _ => KnowledgeMaturityQueueBucket.Other
        };
    }

    internal static KnowledgeMaturityLevel? Next(
        string? jsonType,
        string? rawValue)
    {
        if (!string.Equals(jsonType, "text", StringComparison.Ordinal)
            || KnowledgeViewKey.NormalizeLabel(rawValue) is null)
        {
            return KnowledgeMaturityLevel.Raw;
        }
        if (!TryParseStandard(rawValue, out var level))
        {
            return null;
        }
        return level switch
        {
            KnowledgeMaturityLevel.Raw => KnowledgeMaturityLevel.Excerpt,
            KnowledgeMaturityLevel.Excerpt =>
                KnowledgeMaturityLevel.Organized,
            KnowledgeMaturityLevel.Organized =>
                KnowledgeMaturityLevel.Verified,
            KnowledgeMaturityLevel.Verified =>
                KnowledgeMaturityLevel.Practiced,
            KnowledgeMaturityLevel.Practiced => null,
            _ => null
        };
    }

    internal static bool TryParseStandard(
        string? rawValue,
        out KnowledgeMaturityLevel level)
    {
        var normalized = KnowledgeViewKey.NormalizeLabel(rawValue);
        if (normalized is null)
        {
            level = default;
            return false;
        }

        if (string.Equals(
                normalized,
                "raw",
                StringComparison.OrdinalIgnoreCase))
        {
            level = KnowledgeMaturityLevel.Raw;
            return true;
        }
        if (string.Equals(
                normalized,
                "excerpt",
                StringComparison.OrdinalIgnoreCase))
        {
            level = KnowledgeMaturityLevel.Excerpt;
            return true;
        }
        if (string.Equals(
                normalized,
                "organized",
                StringComparison.OrdinalIgnoreCase))
        {
            level = KnowledgeMaturityLevel.Organized;
            return true;
        }
        if (string.Equals(
                normalized,
                "verified",
                StringComparison.OrdinalIgnoreCase))
        {
            level = KnowledgeMaturityLevel.Verified;
            return true;
        }
        if (string.Equals(
                normalized,
                "practiced",
                StringComparison.OrdinalIgnoreCase))
        {
            level = KnowledgeMaturityLevel.Practiced;
            return true;
        }

        level = default;
        return false;
    }

    internal static KnowledgeMaturityValueType ParseValueType(
        string? jsonType) =>
        jsonType switch
        {
            null => KnowledgeMaturityValueType.Missing,
            "text" => KnowledgeMaturityValueType.String,
            "null" => KnowledgeMaturityValueType.Null,
            "integer" or "real" => KnowledgeMaturityValueType.Number,
            "true" or "false" => KnowledgeMaturityValueType.Boolean,
            "array" => KnowledgeMaturityValueType.Array,
            "object" => KnowledgeMaturityValueType.Object,
            _ => KnowledgeMaturityValueType.Missing
        };

    internal static string ToDatabaseValue(
        KnowledgeMaturityQueueBucket bucket) =>
        bucket switch
        {
            KnowledgeMaturityQueueBucket.PendingOrganization =>
                "pending_organization",
            KnowledgeMaturityQueueBucket.PendingVerification =>
                "pending_verification",
            KnowledgeMaturityQueueBucket.Completed => "completed",
            KnowledgeMaturityQueueBucket.Other => "other",
            _ => throw new ArgumentOutOfRangeException(nameof(bucket))
        };

    internal static string ToDatabaseValue(KnowledgeMaturityLevel level) =>
        level switch
        {
            KnowledgeMaturityLevel.Raw => "raw",
            KnowledgeMaturityLevel.Excerpt => "excerpt",
            KnowledgeMaturityLevel.Organized => "organized",
            KnowledgeMaturityLevel.Verified => "verified",
            KnowledgeMaturityLevel.Practiced => "practiced",
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };

    internal static string? ToDatabaseValue(KnowledgeMaturityLevel? level) =>
        level is null ? null : ToDatabaseValue(level.Value);
}
