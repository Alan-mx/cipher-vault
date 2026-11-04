using CipherVault.Core.Documents;

namespace CipherVault.Core.Drafts;

public enum IdempotencyOperation
{
    DocumentCreate,
    DocumentAppend,
    DocumentUpdate,
    DocumentPatch,
    DocumentDelete,
    DraftCreate,
    DraftUpdate,
    DraftRebase,
    DraftSubmitReview,
    DraftReject,
    DraftDiscard,
    DraftApply,
    McpWriteModeChange,
    SemanticRemoteConsentChange,
    SemanticIndexRebuild
}

public sealed record IdempotencyRecord(
    Guid IdempotencyKey,
    VaultActorKind ActorKind,
    IdempotencyOperation Operation,
    Guid? SessionId,
    string RequestHash,
    string ResultJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public static class IdempotencyOperationMapping
{
    public static string ToDatabaseValue(this IdempotencyOperation value) =>
        value switch
        {
            IdempotencyOperation.DocumentCreate => "document.create",
            IdempotencyOperation.DocumentAppend => "document.append",
            IdempotencyOperation.DocumentUpdate => "document.update",
            IdempotencyOperation.DocumentPatch => "document.patch",
            IdempotencyOperation.DocumentDelete => "document.delete",
            IdempotencyOperation.DraftCreate => "draft.create",
            IdempotencyOperation.DraftUpdate => "draft.update",
            IdempotencyOperation.DraftRebase => "draft.rebase",
            IdempotencyOperation.DraftSubmitReview => "draft.submit_review",
            IdempotencyOperation.DraftReject => "draft.reject",
            IdempotencyOperation.DraftDiscard => "draft.discard",
            IdempotencyOperation.DraftApply => "draft.apply",
            IdempotencyOperation.McpWriteModeChange => "mcp_write_mode.change",
            IdempotencyOperation.SemanticRemoteConsentChange =>
                "semantic_remote_consent.change",
            IdempotencyOperation.SemanticIndexRebuild =>
                "semantic_index.rebuild",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static IdempotencyOperation Parse(string value) =>
        value switch
        {
            "document.create" => IdempotencyOperation.DocumentCreate,
            "document.append" => IdempotencyOperation.DocumentAppend,
            "document.update" => IdempotencyOperation.DocumentUpdate,
            "document.patch" => IdempotencyOperation.DocumentPatch,
            "document.delete" => IdempotencyOperation.DocumentDelete,
            "draft.create" => IdempotencyOperation.DraftCreate,
            "draft.update" => IdempotencyOperation.DraftUpdate,
            "draft.rebase" => IdempotencyOperation.DraftRebase,
            "draft.submit_review" => IdempotencyOperation.DraftSubmitReview,
            "draft.reject" => IdempotencyOperation.DraftReject,
            "draft.discard" => IdempotencyOperation.DraftDiscard,
            "draft.apply" => IdempotencyOperation.DraftApply,
            "mcp_write_mode.change" => IdempotencyOperation.McpWriteModeChange,
            "semantic_remote_consent.change" =>
                IdempotencyOperation.SemanticRemoteConsentChange,
            "semantic_index.rebuild" =>
                IdempotencyOperation.SemanticIndexRebuild,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}
