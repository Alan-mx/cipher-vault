namespace CipherVault.Ipc;

internal static class PipeEncoding
{
    public static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - normalized.Length % 4) % 4),
            '=');
        return Convert.FromBase64String(normalized);
    }
}
