using System.Text.Json;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Server.Api;
using Microsoft.AspNetCore.Http;

namespace CipherVault.Server.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public void DocumentFormatSerializesAsMdAndTxt()
    {
        var options = ApiJson.CreateOptions();

        Assert.Equal(
            "\"md\"",
            JsonSerializer.Serialize(VaultDocumentFormat.Markdown, options));
        Assert.Equal(
            "\"txt\"",
            JsonSerializer.Serialize(VaultDocumentFormat.Text, options));
        Assert.Equal(
            VaultDocumentFormat.Markdown,
            JsonSerializer.Deserialize<VaultDocumentFormat>("\"md\"", options));
    }

    [Theory]
    [InlineData(VaultErrorCode.VaultLocked, 423, "vault_locked")]
    [InlineData(VaultErrorCode.VaultBusy, 409, "vault_busy")]
    [InlineData(VaultErrorCode.VaultLockFailed, 503, "vault_lock_failed")]
    [InlineData(VaultErrorCode.DatabaseError, 500, "database_error")]
    [InlineData(VaultErrorCode.ExportTooLarge, 413, "export_too_large")]
    public void VaultErrorsMapToStableHttpStatuses(
        VaultErrorCode code,
        int expectedStatus,
        string expectedCode)
    {
        var failure = ApiErrorMapper.Map(
            new VaultException(code, "internal details"));

        Assert.Equal(expectedStatus, failure.StatusCode);
        Assert.Equal(expectedCode, failure.Code);
        Assert.DoesNotContain(
            "internal details",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorDetailsAreWhitelisted()
    {
        var context = new DefaultHttpContext();
        context.SetId("0123456789abcdef0123456789abcdef");
        var failure = new ApiFailure(
            409,
            "document_revision_conflict",
            "Revision changed.");
        var error = ApiErrorMapper.CreateError(
            failure,
            context,
            new Dictionary<string, object?>
            {
                ["currentRevision"] = 7,
                ["content"] = "secret"
            });

        Assert.Equal(7, error.Details["currentRevision"]);
        Assert.False(error.Details.ContainsKey("content"));
    }
}
