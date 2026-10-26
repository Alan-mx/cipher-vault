using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Host.Mcp;

namespace CipherVault.Host.Tests;

public sealed class McpToolErrorContractTests
{
    [Fact]
    public void ErrorEnvelopeIncludesRecoveryMetadata()
    {
        var json = BridgeToolEnvelope.Error(
            "revision_conflict",
            "Document revision changed.",
            new Dictionary<string, object?>
            {
                ["currentRevision"] = 9
            });

        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(
            "revision_conflict",
            error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Contains(
            "expectedRevision",
            error.GetProperty("hint").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            9,
            error.GetProperty("details")
                .GetProperty("currentRevision")
                .GetInt32());
    }

    [Theory]
    [InlineData(VaultErrorCode.InvalidCursor, "invalid_cursor")]
    [InlineData(VaultErrorCode.PatchTargetNotFound, "patch_target_not_found")]
    [InlineData(VaultErrorCode.FrontMatterInvalid, "frontmatter_invalid")]
    [InlineData(VaultErrorCode.LinkIndexTooLarge, "link_index_too_large")]
    [InlineData(VaultErrorCode.AccessDenied, "access_denied")]
    [InlineData(VaultErrorCode.Conflict, "revision_conflict")]
    public void VaultDomainErrorsRetainSpecificCodes(
        VaultErrorCode code,
        string expected)
    {
        Assert.Equal(
            expected,
            BridgeToolErrorMapper.ToCode(
                new VaultException(code, "test")));
    }
}
