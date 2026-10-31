namespace CipherVault.Core.KnowledgeGraph;

public sealed record LocalGraphQuery(
    Guid CenterDocumentId,
    int Depth = 2,
    IReadOnlyList<string>? Tags = null,
    string? Folder = null,
    int MaxNodes = 200,
    int MaxEdges = 400);

public sealed record GraphNode(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    IReadOnlyList<string> Tags,
    int Depth,
    int Degree,
    bool IsCenter);

public sealed record GraphEdge(
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    int LinkCount);

public sealed record LocalGraphResult(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    bool Truncated);
