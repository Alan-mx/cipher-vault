using System.Text.Json;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Relations;

public static class RelationEvidenceCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize(IReadOnlyList<RelationEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return JsonSerializer.Serialize(evidence, JsonOptions);
    }

    public static IReadOnlyList<RelationEvidence> Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw Corrupt("Relation evidence must be a JSON array.");
            }

            var result = new List<RelationEvidence>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("documentId", out var documentId)
                    || documentId.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(
                        documentId.GetString(),
                        out var parsedDocumentId)
                    || parsedDocumentId == Guid.Empty
                    || !item.TryGetProperty(
                        "documentRevision",
                        out var documentRevision)
                    || documentRevision.ValueKind != JsonValueKind.Number
                    || !documentRevision.TryGetInt32(out var parsedRevision)
                    || parsedRevision < 1
                    || !item.TryGetProperty("headingPath", out var headingPath)
                    || headingPath.ValueKind != JsonValueKind.Array
                    || !item.TryGetProperty("excerpt", out var excerpt)
                    || excerpt.ValueKind != JsonValueKind.String)
                {
                    throw Corrupt("Relation evidence is invalid.");
                }

                var path = headingPath.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? string.Empty
                        : throw Corrupt(
                            "Relation evidence headingPath is invalid."))
                    .ToArray();
                result.Add(new RelationEvidence(
                    parsedDocumentId,
                    parsedRevision,
                    path,
                    excerpt.GetString() ?? string.Empty));
            }
            return result;
        }
        catch (JsonException exception)
        {
            throw Corrupt("Relation evidence JSON is invalid.", exception);
        }
    }

    private static VaultException Corrupt(
        string message,
        Exception? innerException = null) =>
        new(
            VaultErrorCode.CorruptVault,
            message,
            innerException);
}
