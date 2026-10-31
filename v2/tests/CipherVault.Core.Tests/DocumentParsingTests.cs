using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Tests;

public sealed class DocumentParsingTests
{
    [Fact]
    public void FrontMatterTagsAreAuthoritativeAndPreserved()
    {
        const string content = """
            ---
            title: Example
            tags:
              - Work
              - Personal
            ---
            Body
            """;

        Assert.True(FrontMatterTags.TryExtract(content, out var tags));
        Assert.Equal(["Work", "Personal"], tags);
        Assert.True(FrontMatterDocument.TryParse(content, out var snapshot));
        Assert.Equal(2, snapshot.Fields.Count);
    }

    [Fact]
    public void WikiLinksIgnoreCodeEscapesAndComments()
    {
        const string content = """
            [[One]]
            `[[Two]]`
            ```md
            [[Three]]
            ```
            <!-- [[Four]] -->
            \[[Five]]
            [[Six|Display]]
            """;

        var links = WikiLinkParser.Parse(content);

        Assert.Collection(
            links,
            link => Assert.Equal("One", link.RawTarget),
            link =>
            {
                Assert.Equal("Six", link.RawTarget);
                Assert.Equal("Display", link.DisplayText);
            });
    }

    [Fact]
    public void HeadingPatchReplacesUniqueSectionBody()
    {
        const string content = """
            # Root
            Intro

            ## Child
            Old
            """;
        var command = new VaultPatchCommand(
            "heading",
            ["Root", "Child"],
            null,
            "replace",
            "New",
            null,
            DryRun: false);

        var result = DocumentPatchEngine.Apply(content, command);

        Assert.True(result.Changed);
        Assert.Contains("New", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Old", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontMatterPatchPreservesValueType()
    {
        const string content = """
            ---
            count: 1
            ---
            Body
            """;
        var command = new VaultPatchCommand(
            "frontmatter",
            null,
            "count",
            "replace",
            null,
            JsonSerializer.SerializeToElement(2),
            DryRun: false);

        var result = DocumentPatchEngine.Apply(content, command);

        Assert.Contains("count: 2", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void UnlinkedMentionsIgnoreLinksAndCode()
    {
        const string content = """
            Target appears here.
            [[Target]]
            `Target`
            """;

        var scan = UnlinkedMentionScanner.Scan(content, "Target", maxOccurrences: 5);

        Assert.Equal(1, scan.OccurrenceCount);
        Assert.Single(scan.Occurrences);
    }
}
