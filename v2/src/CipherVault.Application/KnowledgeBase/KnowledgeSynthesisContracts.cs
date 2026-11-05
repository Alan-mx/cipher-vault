namespace CipherVault.Application.KnowledgeBase;

public sealed record KnowledgeSynthesisCreateInput(
    IReadOnlyList<Guid> KnowledgeIds,
    string Topic,
    string? Question,
    Guid KnowledgeTypeId);
