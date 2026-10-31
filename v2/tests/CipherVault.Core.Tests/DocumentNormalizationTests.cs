using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class DocumentNormalizationTests
{
    [Fact]
    public void ExplicitPathIsCaseSensitiveAndNfcCanonical()
    {
        var lower = VirtualPath.Normalize("folder/Note.md", VaultDocumentFormat.Markdown);
        var upper = VirtualPath.Normalize("folder/note.md", VaultDocumentFormat.Markdown);

        Assert.Equal("folder/Note.md", lower.Path);
        Assert.Equal(lower.Path, lower.PathKey);
        Assert.NotEqual(lower.PathKey, upper.PathKey);
    }

    [Theory]
    [InlineData("/absolute.md")]
    [InlineData("folder\\note.md")]
    [InlineData("folder//note.md")]
    [InlineData("folder/../note.md")]
    [InlineData("folder/CON.md")]
    [InlineData("folder/note. ")]
    public void ExplicitInvalidPathIsRejected(string path)
    {
        var exception = Assert.Throws<VaultException>(
            () => VirtualPath.Normalize(path, VaultDocumentFormat.Markdown));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void GeneratedPathSanitizesFilenameAndPreservesCase()
    {
        var path = VirtualPath.Build(
            "A:B * Note",
            "Folder",
            VaultDocumentFormat.Markdown);

        Assert.Equal("Folder/A_B _ Note.md", path.Path);
        Assert.Equal(path.Path, path.PathKey);
    }

    [Fact]
    public void TitleAndTagMustFitScalarLimits()
    {
        Assert.Equal(
            200,
            UnicodeText.CountScalars(new string('a', 200)));
        Assert.Throws<VaultException>(
            () => TitleNormalizer.NormalizeTitle(new string('a', 201)));
        Assert.Throws<VaultException>(
            () => TagNormalizer.Normalize(new string('x', 65)));
    }

    [Fact]
    public void TagsDeduplicateByCaseInsensitiveKeyAndPreserveFirstSpelling()
    {
        var tags = TagNormalizer.NormalizeMany(["  Work  Item ", "work item", "OTHER"]);

        Assert.Collection(
            tags,
            tag =>
            {
                Assert.Equal("OTHER", tag.Name);
                Assert.Equal("OTHER", tag.NormalizedKey);
            },
            tag =>
            {
                Assert.Equal("Work Item", tag.Name);
                Assert.Equal("WORK ITEM", tag.NormalizedKey);
            });
    }
}
