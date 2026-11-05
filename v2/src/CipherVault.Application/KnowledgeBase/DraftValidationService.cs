using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

internal static class DraftValidationService
{
    internal static IReadOnlyList<DraftValidationIssue> Validate(
        DraftRecord record,
        KnowledgeModelSnapshot model)
    {
        if (record.OperationType is DraftOperationType.Archive
            or DraftOperationType.Restore
            or DraftOperationType.Retract)
        {
            try
            {
                KnowledgeBasePayloadParser.RequireEmptyPayload(record.Payload);
                return [];
            }
            catch (VaultException exception)
            {
                return [ToIssue(exception)];
            }
        }
        try
        {
            switch (record.TargetObjectType)
            {
                case DraftTargetObjectType.Source:
                    _ = KnowledgeBasePayloadParser.ParseSource(
                        record.Payload,
                        model);
                    break;
                case DraftTargetObjectType.Knowledge:
                    _ = KnowledgeBasePayloadParser.ParseKnowledge(
                        record.Payload,
                        model);
                    break;
                case DraftTargetObjectType.Relation:
                    _ = KnowledgeBasePayloadParser.ParseRelation(
                        record.Payload,
                        model);
                    break;
            }
            return [];
        }
        catch (VaultException exception)
        {
            return [ToIssue(exception)];
        }
    }

    private static DraftValidationIssue ToIssue(VaultException exception)
    {
        var fieldPath = exception.Details.TryGetValue(
                "fieldPath",
                out var value)
            && value is string path
            && !string.IsNullOrWhiteSpace(path)
                ? path
                : "$";
        return new DraftValidationIssue(
            fieldPath,
            exception.Message,
            DraftValidationSeverity.Error);
    }
}
