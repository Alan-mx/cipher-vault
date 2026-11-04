using System.Text;
using CipherVault.Core.Errors;
using CipherVault.Core.Relations;

namespace CipherVault.Application.Relations;

internal static class RelationSuggestionValidator
{
    internal const int MaxReasonScalars = 1_000;
    internal const int MaxSubmitterScalars = 128;
    internal const int MaxEvidence = 3;
    internal const int MaxEvidenceExcerptScalars = 500;
    internal const int MaxHeadingSegments = 64;
    internal const int MaxHeadingSegmentScalars = 256;
    internal const int MaxPayloadBytes = 64 * 1024;

    internal static string NormalizeReason(string? reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        var count = normalized.EnumerateRunes().Count();
        if (count is < 1 or > MaxReasonScalars)
        {
            throw Invalid(
                $"reason must contain 1 to {MaxReasonScalars} Unicode scalars.");
        }
        return normalized;
    }

    internal static (
        string? ModelName,
        string? AgentName,
        string? ClientName) NormalizeSubmitter(
            string? modelName,
            string? agentName,
            string? clientName)
    {
        var model = NormalizeOptionalName(modelName, "modelName");
        var agent = NormalizeOptionalName(agentName, "agentName");
        var client = NormalizeOptionalName(clientName, "clientName");
        if (model is null && agent is null && client is null)
        {
            throw Invalid(
                "At least one of modelName, agentName or clientName is required.");
        }
        return (model, agent, client);
    }

    internal static IReadOnlyList<RelationEvidence> NormalizeEvidence(
        IReadOnlyList<RelationEvidence>? evidence,
        Guid sourceDocumentId,
        int sourceDocumentRevision,
        Guid targetDocumentId,
        int targetDocumentRevision)
    {
        var source = evidence ?? [];
        if (source.Count > MaxEvidence)
        {
            throw Invalid($"evidence supports at most {MaxEvidence} items.");
        }

        var result = new List<RelationEvidence>(source.Count);
        foreach (var item in source)
        {
            if (item.DocumentId != sourceDocumentId
                && item.DocumentId != targetDocumentId)
            {
                throw Invalid(
                    "Evidence may reference only the source or target document.");
            }
            var expectedRevision = item.DocumentId == sourceDocumentId
                ? sourceDocumentRevision
                : targetDocumentRevision;
            if (item.DocumentRevision != expectedRevision)
            {
                throw Invalid(
                    "Evidence documentRevision must match the suggestion document revision.");
            }
            if (item.HeadingPath.Count > MaxHeadingSegments)
            {
                throw Invalid(
                    $"Evidence headingPath supports at most {MaxHeadingSegments} segments.");
            }
            var headingPath = new List<string>(item.HeadingPath.Count);
            foreach (var segment in item.HeadingPath)
            {
                var value = segment?.Trim() ?? string.Empty;
                if (value.Length == 0
                    || value.EnumerateRunes().Count() >
                        MaxHeadingSegmentScalars)
                {
                    throw Invalid(
                        "Evidence headingPath contains an invalid segment.");
                }
                headingPath.Add(value);
            }
            var excerpt = item.Excerpt?.Trim() ?? string.Empty;
            var excerptScalars = excerpt.EnumerateRunes().Count();
            if (excerptScalars is < 1 or > MaxEvidenceExcerptScalars)
            {
                throw Invalid(
                    $"Evidence excerpt must contain 1 to {MaxEvidenceExcerptScalars} Unicode scalars.");
            }
            result.Add(new RelationEvidence(
                item.DocumentId,
                item.DocumentRevision,
                headingPath.ToArray(),
                excerpt));
        }
        return result.ToArray();
    }

    internal static void ValidatePayloadSize(
        string reason,
        string? modelName,
        string? agentName,
        string? clientName,
        IReadOnlyList<RelationEvidence> evidence)
    {
        var bytes = Encoding.UTF8.GetByteCount(reason)
            + Encoding.UTF8.GetByteCount(modelName ?? string.Empty)
            + Encoding.UTF8.GetByteCount(agentName ?? string.Empty)
            + Encoding.UTF8.GetByteCount(clientName ?? string.Empty)
            + Encoding.UTF8.GetByteCount(
                RelationEvidenceCodec.Serialize(evidence));
        if (bytes > MaxPayloadBytes)
        {
            throw Invalid(
                $"Relation suggestion payload exceeds {MaxPayloadBytes} bytes.");
        }
    }

    private static string? NormalizeOptionalName(
        string? value,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalized = value.Trim();
        if (normalized.EnumerateRunes().Count() > MaxSubmitterScalars)
        {
            throw Invalid(
                $"{fieldName} must not exceed {MaxSubmitterScalars} Unicode scalars.");
        }
        return normalized;
    }

    private static VaultException Invalid(string message) =>
        new(VaultErrorCode.InvalidInput, message);
}
