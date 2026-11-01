using CipherVault.Core.Backup;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class BackupFileNameTests
{
    [Fact]
    public void CreateUsesUtcTimestampAndLowercaseRandomSuffix()
    {
        var timestamp = new DateTimeOffset(
            2026,
            11,
            1,
            12,
            34,
            56,
            TimeSpan.FromHours(8));

        var result = BackupFileName.Create(timestamp);

        Assert.Equal(
            "20261101-043456",
            result.UtcTimestamp.ToString("yyyyMMdd-HHmmss"));
        Assert.Matches(
            "^cipher-vault-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\\.cvbackup$",
            result.Value);
        Assert.Matches("^[0-9a-f]{8}$", result.RandomSuffix);
    }

    [Fact]
    public void ParseRoundTripsCanonicalFilename()
    {
        var created = BackupFileName.Create(DateTimeOffset.UtcNow);

        var parsed = BackupFileName.Parse(created.Value);

        Assert.Equal(created, parsed);
    }

    [Theory]
    [InlineData("../cipher-vault-20261101-123456-deadbeef.cvbackup")]
    [InlineData("cipher-vault-20261101-123456-DEADBEEF.cvbackup")]
    [InlineData("cipher-vault-20261301-123456-deadbeef.cvbackup")]
    [InlineData("cipher-vault-20261101-123456-deadbeef.db")]
    [InlineData("C:\\cipher-vault-20261101-123456-deadbeef.cvbackup")]
    public void InvalidFilenamesAreRejected(string value)
    {
        Assert.False(BackupFileName.TryParse(value, out _));
        var exception = Assert.Throws<VaultException>(
            () => BackupFileName.Parse(value));
        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }
}
