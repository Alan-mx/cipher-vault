using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class KnowledgeMaturityPatchEngineTests
{
    [Fact]
    public void SetCreatesFrontMatterForMarkdownWithoutOne()
    {
        var result = DocumentPatchEngine.Apply(
            "# Body",
            SetCommand("raw"));

        Assert.True(result.Changed);
        Assert.StartsWith(
            "---\nmaturity: raw\n---\n# Body",
            result.Content,
            StringComparison.Ordinal);
        Assert.True(FrontMatterDocument.TryParse(result.Content, out var parsed));
        Assert.Equal(
            "\"raw\"",
            Assert.Single(
                parsed.Fields,
                field => field.Key == "maturity").Value.GetRawText());
    }

    [Fact]
    public void SetPreservesCrLfAndInsertsBeforeClosingDelimiter()
    {
        var result = DocumentPatchEngine.Apply(
            "---\r\nstatus: active\r\n---\r\n\r\nBody",
            SetCommand("excerpt"));

        Assert.Contains(
            "status: active\r\nmaturity: excerpt\r\n---",
            result.Content,
            StringComparison.Ordinal);
        Assert.True(FrontMatterDocument.TryParse(result.Content, out var parsed));
        Assert.Equal(2, parsed.Fields.Count);
    }

    [Fact]
    public void SetRejectsNonMaturityKey()
    {
        var exception = Assert.Throws<VaultException>(() =>
            DocumentPatchEngine.Apply(
                "---\nstatus: active\n---\nBody",
                SetCommand("raw") with
                {
                    FrontMatterKey = "status"
                }));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    private static VaultPatchCommand SetCommand(string value) =>
        new(
            "frontmatter",
            null,
            "maturity",
            "set",
            null,
            JsonSerializer.SerializeToElement(value),
            DryRun: false);
}
