using CipherVault.Core.Documents;

namespace CipherVault.Core.Tests;

public sealed class MarkdownLinkWriterTests
{
    [Theory]
    [InlineData(
        "notes/projects/source.md",
        "notes/shared/My Note.md",
        "Redis 缓存",
        "../shared/My%20Note.md#Redis%20%E7%BC%93%E5%AD%98")]
    [InlineData(
        "source.md",
        "folder/target.md",
        null,
        "folder/target.md")]
    [InlineData(
        "a/b/source.md",
        "a/b/target.md",
        "Top",
        "target.md#Top")]
    public void BuildsShortestRelativeDestination(
        string source,
        string target,
        string? heading,
        string expected)
    {
        Assert.Equal(
            expected,
            MarkdownLinkWriter.BuildRelativeDestination(
                source,
                target,
                heading));
    }

    [Fact]
    public void ReplacesOnlyDestinationSpan()
    {
        const string content = "[text](<old path.md> \"title\")";

        var result = MarkdownLinkWriter.ReplaceDestination(
            content,
            7,
            20,
            "new.md");

        Assert.Equal("[text](new.md \"title\")", result);
    }
}
