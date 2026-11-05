using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Server.Api.Contracts;

public sealed record CreateUnifiedDraftApiRequest(
    string? TargetObjectType,
    string? OperationType,
    Guid? TargetId,
    int? BaseRevision,
    JsonElement Payload)
{
    public CreateDraftInput ToDomain() =>
        new(
            ParseTarget(TargetObjectType),
            ParseOperation(OperationType),
            TargetId,
            BaseRevision,
            Payload.Clone());

    private static DraftTargetObjectType ParseTarget(string? value) =>
        value switch
        {
            "knowledge" => DraftTargetObjectType.Knowledge,
            "source" => DraftTargetObjectType.Source,
            "relation" => DraftTargetObjectType.Relation,
            _ => throw Invalid("targetObjectType")
        };

    private static DraftOperationType ParseOperation(string? value) =>
        value switch
        {
            "create" => DraftOperationType.Create,
            "update" => DraftOperationType.Update,
            "archive" => DraftOperationType.Archive,
            "restore" => DraftOperationType.Restore,
            "retract" => DraftOperationType.Retract,
            _ => throw Invalid("operationType")
        };

    private static ApiRequestException Invalid(string name) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.DraftOperationInvalid,
            $"{name} is invalid.");
}

public sealed record UpdateUnifiedDraftApiRequest(
    int ExpectedDraftRevision,
    JsonElement Payload);

public sealed record RebaseUnifiedDraftApiRequest(
    int ExpectedDraftRevision,
    int ObservedTargetRevision);

public sealed record UnifiedDraftRevisionApiRequest(
    int ExpectedDraftRevision);
