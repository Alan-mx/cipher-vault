using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeGraph;

namespace CipherVault.Application.Tests;

public sealed class GraphServiceTests
{
    [Fact]
    public async Task GraphUsesDepthFiltersAndDeterministicCaps()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var a = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "A",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Root]\n---\n[[B]]",
                null),
            actor);
        var b = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "B",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Work]\n---\n[[C]]",
                null),
            actor);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "C",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Personal]\n---\nC",
                null),
            actor);

        var depthOne = await fixture.Graph.GetLocalGraphAsync(
            new LocalGraphQuery(a.DocumentId, Depth: 1));
        Assert.Equal(["A", "B"], depthOne.Nodes.Select(node => node.Title));
        Assert.Single(depthOne.Edges);

        var depthTwo = await fixture.Graph.GetLocalGraphAsync(
            new LocalGraphQuery(a.DocumentId, Depth: 2));
        Assert.Equal(["A", "B", "C"], depthTwo.Nodes.Select(node => node.Title));

        var filtered = await fixture.Graph.GetLocalGraphAsync(
            new LocalGraphQuery(
                a.DocumentId,
                Depth: 2,
                Tags: ["Work"]));
        Assert.Equal(["A", "B"], filtered.Nodes.Select(node => node.Title));
        Assert.True(filtered.Nodes[0].IsCenter);

        var capped = await fixture.Graph.GetLocalGraphAsync(
            new LocalGraphQuery(
                a.DocumentId,
                Depth: 2,
                MaxNodes: 1));
        Assert.Single(capped.Nodes);
        Assert.Empty(capped.Edges);
        Assert.True(capped.Truncated);
        Assert.Equal(b.DocumentId, depthOne.Nodes[1].DocumentId);
    }

    [Fact]
    public async Task DeletedCenterReturnsNotFound()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Deleted",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null),
            actor);
        await fixture.Documents.DeleteAsync(created.DocumentId, 1, actor);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Graph.GetLocalGraphAsync(
                new LocalGraphQuery(created.DocumentId)));
        Assert.Equal(VaultErrorCode.DocumentNotFound, exception.Code);
    }
}
