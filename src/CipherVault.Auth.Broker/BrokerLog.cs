using System.IO;
using System.Text;
using System.Text.Json;

namespace CipherVault.Auth.Broker;

public sealed class BrokerLog
{
    private const int MaxMessageLength = 200;
    private readonly object _sync = new();
    private readonly string _path;

    public BrokerLog(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CipherVault",
        "Logs",
        "broker.log");

    public void Write(
        string eventName,
        string result,
        Guid? requestId = null,
        string? kind = null,
        string? errorCode = null,
        string? message = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var record = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                @event = eventName,
                requestId,
                kind,
                result,
                errorCode,
                message = Sanitize(message)
            });
            lock (_sync)
            {
                File.AppendAllText(
                    _path,
                    record + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }
        var singleLine = message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return singleLine.Length <= MaxMessageLength
            ? singleLine
            : singleLine[..MaxMessageLength];
    }
}
