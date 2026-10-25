using CipherVault.Core.Documents;
using CipherVault.Ui.Wpf;

namespace CipherVault.Ui.Wpf.Tests;

public sealed class LinkSuggestionEngineTests
{
    [Fact]
    public void FindCompletionDetectsOpenWikiLink()
    {
        var completion = LinkSuggestionEngine.FindCompletion(
            "first line\n参见 [[项目",
            "first line\n参见 [[项目".Length);

        Assert.NotNull(completion);
        Assert.Equal("first line\n参见 ".Length, completion.StartOffset);
        Assert.Equal("项目", completion.Query);
    }

    [Fact]
    public void FindCompletionRejectsClosedOrNonTargetContext()
    {
        Assert.Null(LinkSuggestionEngine.FindCompletion(
            "[[项目]]",
            "[[项目".Length));
        Assert.Null(LinkSuggestionEngine.FindCompletion(
            "[[项目|显示",
            "[[项目|显示".Length));
        Assert.Null(LinkSuggestionEngine.FindCompletion(
            "ordinary text",
            "ordinary text".Length));
    }

    [Fact]
    public void RankUsesTitleThenPathAndDisambiguatesDuplicateTitles()
    {
        var current = Guid.NewGuid();
        var documents = new[]
        {
            CreateDocument(current, "项目", "a/项目.md"),
            CreateDocument(Guid.NewGuid(), "项目", "b/项目.md"),
            CreateDocument(Guid.NewGuid(), "项目计划", "计划/项目计划.md"),
            CreateDocument(Guid.NewGuid(), "其他", "其他.md")
        };

        var suggestions = LinkSuggestionEngine.Rank(
            documents,
            current,
            "项目");

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("项目", suggestions[0].Title);
        Assert.Equal("b/项目", suggestions[0].InsertTarget);
        Assert.Equal("项目计划", suggestions[1].Title);
        Assert.Equal("项目计划", suggestions[1].InsertTarget);
    }

    [Fact]
    public void RankSkipsTargetsThatCannotBeRepresentedByWikiSyntax()
    {
        var documents = new[]
        {
            CreateDocument(Guid.NewGuid(), "bad#title", "bad#title.md"),
            CreateDocument(Guid.NewGuid(), "good", "good.md")
        };

        var suggestions = LinkSuggestionEngine.Rank(documents, null, string.Empty);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("good", suggestion.Title);
    }

    private static VaultCatalogItem CreateDocument(
        Guid id,
        string title,
        string virtualPath)
    {
        return new VaultCatalogItem(
            id,
            title,
            virtualPath,
            "md",
            1,
            [],
            [],
            DateTimeOffset.Parse("2026-10-25T00:00:00Z"));
    }
}
