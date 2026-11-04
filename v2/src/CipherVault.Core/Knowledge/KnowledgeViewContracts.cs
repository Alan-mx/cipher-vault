using CipherVault.Core.Documents;
using System.Text.Json.Serialization;

namespace CipherVault.Core.Knowledge;

public enum KnowledgeViewField
{
    Project,
    Type,
    Maturity
}

public sealed record KnowledgeFacetItem(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Key,
    string Label,
    int DocumentCount,
    bool IsUnset);

public sealed record KnowledgeFacetPage(
    KnowledgeViewField Field,
    IReadOnlyList<KnowledgeFacetItem> Items,
    bool Truncated);

public sealed record KnowledgeViewItem(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    VaultDocumentFormat Format,
    int Revision,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeViewPage(
    IReadOnlyList<KnowledgeViewItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);
