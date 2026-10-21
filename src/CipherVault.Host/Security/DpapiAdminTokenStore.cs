using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Host.Security;

public sealed class DpapiAdminTokenStore : IAdminTokenStore
{
    private const int TokenBytes = 32;
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("CipherVault.Host.AdminToken.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _tokenPath;

    public DpapiAdminTokenStore(string? tokenPath = null)
    {
        _tokenPath = Path.GetFullPath(tokenPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CipherVault",
            "admin-token.bin"));
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_tokenPath))
            {
                return ReadToken();
            }

            var directory = Path.GetDirectoryName(_tokenPath)
                ?? throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Admin token path has no parent directory.");
            Directory.CreateDirectory(directory);
            VaultFileSecurity.ProtectDirectory(directory);
            var token = CreateToken();
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token),
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                using var stream = new FileStream(
                    _tokenPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
                VaultFileSecurity.ProtectFile(_tokenPath);
                return token;
            }
            catch (IOException) when (File.Exists(_tokenPath))
            {
                return ReadToken();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ReadToken()
    {
        try
        {
            var protectedBytes = File.ReadAllBytes(_tokenPath);
            if (protectedBytes.Length is 0 or > 4096)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Admin token file is invalid.");
            }
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                var token = Encoding.UTF8.GetString(plainBytes);
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new VaultException(
                        VaultErrorCode.StorageError,
                        "Admin token file is empty.");
                }
                return token;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or
            IOException or
            UnauthorizedAccessException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Admin token file could not be read.",
                exception);
        }
    }

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        try
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
