using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Backup;

public sealed record BackupFileName(
    string Value,
    DateTimeOffset UtcTimestamp,
    string RandomSuffix)
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";
    private static readonly Regex Pattern = new(
        "^cipher-vault-(?<timestamp>[0-9]{8}-[0-9]{6})-(?<suffix>[0-9a-f]{8})\\.cvbackup$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static BackupFileName Create(DateTimeOffset utcNow)
    {
        var universal = utcNow.ToUniversalTime();
        var timestamp = new DateTimeOffset(
            universal.Ticks - (universal.Ticks % TimeSpan.TicksPerSecond),
            TimeSpan.Zero);
        var suffix = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var value =
            $"{BackupConstants.BackupFilePrefix}{timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{suffix}{BackupConstants.BackupExtension}";
        return new BackupFileName(value, timestamp, suffix);
    }

    public static BackupFileName Parse(string value)
    {
        if (!TryParse(value, out var result))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Backup filename is invalid.");
        }

        return result;
    }

    public static bool TryParse(
        string? value,
        out BackupFileName result)
    {
        result = null!;
        if (string.IsNullOrEmpty(value)
            || value.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return false;
        }

        var match = Pattern.Match(value);
        if (!match.Success
            || !DateTimeOffset.TryParseExact(
                match.Groups["timestamp"].Value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return false;
        }

        result = new BackupFileName(
            value,
            timestamp.ToUniversalTime(),
            match.Groups["suffix"].Value);
        return true;
    }
}
