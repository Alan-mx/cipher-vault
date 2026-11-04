using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class DocumentIdentityTests
{
    private const string DocumentIdText =
        "7b29a2e1-1111-4222-8333-123456789abc";
    private static readonly Guid DocumentId = Guid.Parse(DocumentIdText);

    [Fact]
    public void ParsesAndFormatsCanonicalIdentity()
    {
        Assert.True(DocumentIdentity.TryParse(DocumentIdText.ToUpperInvariant(), out var id));
        Assert.Equal(DocumentId, id);
        Assert.Equal(DocumentIdText, DocumentIdentity.Format(id));

        Assert.False(DocumentIdentity.TryParse(null, out _));
        Assert.False(DocumentIdentity.TryParse(string.Empty, out _));
        Assert.False(DocumentIdentity.TryParse(Guid.Empty.ToString("D"), out _));
        Assert.False(DocumentIdentity.TryParse($"{{{DocumentIdText}}}", out _));
    }

    [Fact]
    public void InjectsIdIntoExistingFrontmatterWithoutReorderingFields()
    {
        const string content = """
            ---
            title: Existing
            tags: [One]
            ---
            Body
            """;

        var updated = DocumentIdentity.EnsureMarkdownIdentity(
            content,
            DocumentId,
            DocumentIdentityMode.NewDocument);

        Assert.StartsWith(
            $"---\nid: {DocumentIdText}\ntitle: Existing",
            updated,
            StringComparison.Ordinal);
        Assert.Contains("tags: [One]", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatesFrontmatterWhenMissingAndPreservesCrLf()
    {
        var updated = DocumentIdentity.EnsureMarkdownIdentity(
            "# Body\r\n",
            DocumentId,
            DocumentIdentityMode.NewDocument);

        Assert.Equal(
            $"---\r\nid: {DocumentIdText}\r\n---\r\n# Body\r\n",
            updated);
    }

    [Fact]
    public void ExistingDocumentRestoresMissingIdAndPreservesMatchingId()
    {
        var injected = DocumentIdentity.EnsureMarkdownIdentity(
            "Body",
            DocumentId,
            DocumentIdentityMode.ExistingDocument);
        Assert.Contains($"id: {DocumentIdText}", injected, StringComparison.Ordinal);

        var preserved = DocumentIdentity.EnsureMarkdownIdentity(
            injected,
            DocumentId,
            DocumentIdentityMode.ExistingDocument);
        Assert.Equal(injected, preserved);
    }

    [Fact]
    public void RejectsInvalidAndMismatchedIdentity()
    {
        var invalid = Assert.Throws<VaultException>(
            () => DocumentIdentity.InspectMarkdown(
                "---\nid: not-a-guid\n---\nbody"));
        Assert.Equal(VaultErrorCode.InvalidDocumentId, invalid.Code);

        var mismatch = Assert.Throws<VaultException>(
            () => DocumentIdentity.EnsureMarkdownIdentity(
                $"---\nid: {Guid.NewGuid():D}\n---\nbody",
                DocumentId,
                DocumentIdentityMode.ExistingDocument));
        Assert.Equal(VaultErrorCode.DocumentIdMismatch, mismatch.Code);
    }

    [Fact]
    public void RejectsDuplicateIdBeforeGenericYamlError()
    {
        var exception = Assert.Throws<VaultException>(
            () => DocumentIdentity.InspectMarkdown(
                $"""
                 ---
                 id: {DocumentIdText}
                 id: {DocumentIdText}
                 ---
                 body
                 """));

        Assert.Equal(VaultErrorCode.InvalidDocumentId, exception.Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("123")]
    public void RejectsNonStringIdentityValues(string value)
    {
        var exception = Assert.Throws<VaultException>(
            () => DocumentIdentity.InspectMarkdown(
                $"---\nid: {value}\n---\nbody"));

        Assert.Equal(VaultErrorCode.InvalidDocumentId, exception.Code);
    }

    [Fact]
    public void IdentityFieldNameIsCaseSensitive()
    {
        var identity = DocumentIdentity.InspectMarkdown(
            "---\nID: 00000000-0000-4000-8000-000000000001\n---\nbody");

        Assert.False(identity.HasId);
        Assert.Null(identity.DocumentId);
    }

    [Fact]
    public void UnclosedFrontmatterIsNotTreatedAsAbsent()
    {
        var exception = Assert.Throws<VaultException>(
            () => DocumentIdentity.EnsureMarkdownIdentity(
                "---\ntitle: broken\nbody",
                DocumentId,
                DocumentIdentityMode.ExistingDocument));

        Assert.Equal(VaultErrorCode.FrontMatterInvalid, exception.Code);
    }

    [Fact]
    public void CopyAsNewReplacesOnlyTopLevelId()
    {
        var oldId = Guid.NewGuid();
        const string content = """
            ---
            id: 00000000-0000-4000-8000-000000000001
            nested:
              id: nested
            ---
            Body
            """;

        var copied = DocumentIdentity.EnsureMarkdownIdentity(
            content.Replace(
                "00000000-0000-4000-8000-000000000001",
                oldId.ToString("D"),
                StringComparison.Ordinal),
            DocumentId,
            DocumentIdentityMode.CopyAsNew);

        Assert.Contains($"id: {DocumentIdText}", copied, StringComparison.Ordinal);
        Assert.DoesNotContain(oldId.ToString("D"), copied, StringComparison.Ordinal);
        Assert.Contains("id: nested", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentFingerprintIgnoresIdAndNewlineDifferences()
    {
        var first = DocumentContentFingerprint.Compute(
            $"---\nid: {DocumentIdText}\ntitle: One\n---\nBody\n",
            VaultDocumentFormat.Markdown);
        var second = DocumentContentFingerprint.Compute(
            $"\uFEFF---\r\nid: {Guid.NewGuid():D}\r\ntitle: One\r\n---\r\nBody\r\n",
            VaultDocumentFormat.Markdown);

        Assert.Equal(first, second);
    }
}
