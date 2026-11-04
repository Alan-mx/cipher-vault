using CipherVault.Core.Documents;

namespace CipherVault.Core.Tests;

public sealed class DocumentLinkParsingTests
{
    private static readonly NormalizedVirtualPath SourcePath =
        VirtualPath.Normalize(
            "notes/projects/source.md",
            VaultDocumentFormat.Markdown);

    [Fact]
    public void ParsesRelativeDestinationWithFragment()
    {
        const string content =
            "prefix [Redis](../shared/My%20Note.md#Redis%20%E7%BC%93%E5%AD%98) suffix";

        var link = Assert.Single(DocumentLinkParser.Parse(content, SourcePath));

        Assert.Equal(DocumentLinkKind.Markdown, link.Kind);
        Assert.Equal("../shared/My%20Note.md#Redis%20%E7%BC%93%E5%AD%98", link.RawTarget);
        Assert.Equal("notes/shared/My Note.md", link.CanonicalTargetKey);
        Assert.Equal("Redis", link.DisplayText);
        Assert.Equal("Redis 缓存", link.TargetHeading);
        Assert.Equal("REDIS 缓存", link.TargetHeadingKey);
        Assert.Equal(7, link.StartOffset);
        Assert.Equal(content.Length - 7, link.EndOffset);
        Assert.Equal(15, link.DestinationStartOffset);
        Assert.Equal(content.Length - 8, link.DestinationEndOffset);
    }

    [Fact]
    public void ParsesAngleDestinationTitleAndBalancedParentheses()
    {
        const string content = """
            [First](<../notes/a note.md#Heading> "title")
            [Second](target(with-parens).md)
            """;

        var links = DocumentLinkParser.Parse(content, SourcePath);

        Assert.Collection(
            links,
            link =>
            {
                Assert.Equal("../notes/a note.md#Heading", link.RawTarget);
                Assert.Equal("notes/notes/a note.md", link.CanonicalTargetKey);
                Assert.Equal("<../notes/a note.md#Heading>", content[
                    link.DestinationStartOffset!.Value..link.DestinationEndOffset!.Value]);
            },
            link =>
            {
                Assert.Equal("target(with-parens).md", link.RawTarget);
                Assert.Equal(
                    "notes/projects/target(with-parens).md",
                    link.CanonicalTargetKey);
            });
    }

    [Fact]
    public void ParsesEscapesWithoutDoubleDecodingPercent()
    {
        const string content =
            "[Escaped](a\\(b\\).MD) [Percent](a%2520b.md) [Invalid](a%ZZ.md)";

        var links = DocumentLinkParser.Parse(content, SourcePath);

        Assert.Collection(
            links,
            link => Assert.Equal(
                "notes/projects/a(b).md",
                link.CanonicalTargetKey),
            link => Assert.Equal(
                "notes/projects/a%20b.md",
                link.CanonicalTargetKey));
    }

    [Theory]
    [InlineData("[x](https://example.com/a.md)")]
    [InlineData("[x](//example.com/a.md)")]
    [InlineData("[x](mailto:user@example.com)")]
    [InlineData("[x](a.pdf)")]
    [InlineData("[x](a.md?download=1)")]
    [InlineData("[x](a\\b.md)")]
    [InlineData("[x](../../../a.md)")]
    [InlineData("![x](a.md)")]
    [InlineData("[x][ref]")]
    [InlineData("<https://example.com/a.md>")]
    [InlineData("`[x](a.md)`")]
    [InlineData("<!-- [x](a.md) -->")]
    [InlineData("```md\n[x](a.md)\n```")]
    public void IgnoresUnsupportedOrProtectedLinks(string content)
    {
        Assert.Empty(DocumentLinkParser.Parse(content, SourcePath));
    }

    [Fact]
    public void ParsesRootPathAndNormalizesExtensionCase()
    {
        var link = Assert.Single(
            DocumentLinkParser.Parse(
                "[Root](/Index.MD#Top)",
                SourcePath));

        Assert.Equal("Index.md", link.CanonicalTargetKey);
        Assert.Equal("Top", link.TargetHeading);
    }

    [Fact]
    public void UnifiedParserOrdersWikiAndMarkdownAndAvoidsOverlap()
    {
        const string content =
            "[Markdown](a.md) [[Wiki]] [Second](./b.txt)";

        var links = DocumentLinkParser.Parse(content, SourcePath);

        Assert.Collection(
            links,
            link => Assert.Equal(DocumentLinkKind.Markdown, link.Kind),
            link => Assert.Equal(DocumentLinkKind.Wiki, link.Kind),
            link => Assert.Equal(DocumentLinkKind.Markdown, link.Kind));
        Assert.Equal(
            links.OrderBy(link => link.StartOffset).Select(link => link.StartOffset),
            links.Select(link => link.StartOffset));
    }

    [Fact]
    public void PercentDecodesFragmentOnceAndUsesNfcHeadingKey()
    {
        var link = Assert.Single(
            DocumentLinkParser.Parse(
                "[x](a.md#Cafe%CC%81%20%20Plan)",
                SourcePath));

        Assert.Equal("Café  Plan", link.TargetHeading);
        Assert.Equal("CAFÉ PLAN", link.TargetHeadingKey);
    }
}
