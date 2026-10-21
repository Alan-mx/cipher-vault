using System.Security.Cryptography;
using System.Text;

namespace CipherVault.Host.Contracts;

public static class HostConfigurationFingerprint
{
    public static string Compute(
        string vaultPath,
        string configPath,
        string bindAddress,
        int port,
        string mcpPath,
        string adminPath)
    {
        var normalized = string.Join(
            "\n",
            Path.GetFullPath(vaultPath).ToUpperInvariant(),
            Path.GetFullPath(configPath).ToUpperInvariant(),
            bindAddress.ToLowerInvariant(),
            port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mcpPath,
            adminPath);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
