using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class DpapiAdminTokenStoreTests
{
    [Fact]
    public async Task TokenRoundTripsAndIsNotStoredAsPlaintext()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-admin-token-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var tokenPath = Path.Combine(root, "admin-token.bin");
        try
        {
            var first = new DpapiAdminTokenStore(tokenPath);
            var token = await first.GetTokenAsync();
            var stored = await File.ReadAllBytesAsync(tokenPath);
            var storedText = System.Text.Encoding.UTF8.GetString(stored);

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(File.Exists(tokenPath));
            Assert.DoesNotContain(token, storedText, StringComparison.Ordinal);

            var second = new DpapiAdminTokenStore(tokenPath);
            Assert.Equal(token, await second.GetTokenAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
