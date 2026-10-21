using System.IO;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Client.Security;

public sealed class DpapiAdminTokenStore : IAdminTokenStore
{
    private const int TokenBytes = 32;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;

    public DpapiAdminTokenStore(string? tokenDirectory = null)
    {
        _directory = Path.GetFullPath(tokenDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CipherVault"));
    }

    public async Task<string> GetTokenAsync(
        HostAdminClientKind clientKind,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokenPath = GetTokenPath(clientKind);
            return File.Exists(tokenPath)
                ? ReadToken(clientKind, tokenPath)
                : CreateToken(clientKind, tokenPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostAdminClientKind?> AuthenticateAsync(
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var presented = Encoding.UTF8.GetBytes(rawToken);
            try
            {
                foreach (var clientKind in new[]
                         {
                             HostAdminClientKind.Ui,
                             HostAdminClientKind.Cli
                         })
                {
                    var tokenPath = GetTokenPath(clientKind);
                    if (!File.Exists(tokenPath))
                    {
                        continue;
                    }
                    var expected = Encoding.UTF8.GetBytes(
                        ReadToken(clientKind, tokenPath));
                    try
                    {
                        if (CryptographicOperations.FixedTimeEquals(
                                presented,
                                expected))
                        {
                            return clientKind;
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(expected);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(presented);
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CreateToken(
        HostAdminClientKind clientKind,
        string tokenPath)
    {
        Directory.CreateDirectory(_directory);
        VaultFileSecurity.ProtectDirectory(_directory);
        var token = CreateToken();
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                tokenBytes,
                GetEntropy(clientKind),
                DataProtectionScope.CurrentUser);
            try
            {
                using var stream = new FileStream(
                    tokenPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
                VaultFileSecurity.ProtectFile(tokenPath);
                return token;
            }
            catch (IOException) when (File.Exists(tokenPath))
            {
                return ReadToken(clientKind, tokenPath);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
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
                "Admin token file could not be created.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private string ReadToken(
        HostAdminClientKind clientKind,
        string tokenPath)
    {
        try
        {
            var protectedBytes = File.ReadAllBytes(tokenPath);
            if (protectedBytes.Length is 0 or > 4096)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Admin token file is invalid.");
            }
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                GetEntropy(clientKind),
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

    private string GetTokenPath(HostAdminClientKind clientKind)
    {
        return Path.Combine(
            _directory,
            clientKind == HostAdminClientKind.Cli
                ? "admin-cli-token.bin"
                : "admin-ui-token.bin");
    }

    private static byte[] GetEntropy(HostAdminClientKind clientKind)
    {
        return Encoding.UTF8.GetBytes(
            clientKind == HostAdminClientKind.Cli
                ? "CipherVault.Host.AdminToken.v2.cli"
                : "CipherVault.Host.AdminToken.v2.ui");
    }

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
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
