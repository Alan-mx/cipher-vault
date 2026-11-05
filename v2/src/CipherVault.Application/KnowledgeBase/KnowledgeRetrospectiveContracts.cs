namespace CipherVault.Application.KnowledgeBase;

public sealed record KnowledgeRetrospectiveCreateInput(
    IReadOnlyList<Guid> KnowledgeIds,
    string Topic);
