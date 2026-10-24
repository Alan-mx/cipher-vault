using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Security;

namespace CipherVault.Host.Operations;

public sealed record BrokerLifecycleLogEvent(
    DateTimeOffset OccurredAt,
    string EventType,
    string Stage,
    string Result,
    uint? ProcessId = null,
    DateTimeOffset? ProcessStartedAt = null,
    int? RetryCount = null,
    string? ErrorType = null,
    int? Win32ErrorCode = null,
    string? Message = null);

public interface IBrokerLifecycleLog
{
    void Write(BrokerLifecycleLogEvent lifecycleEvent);
}

public sealed class NullBrokerLifecycleLog : IBrokerLifecycleLog
{
    public static readonly NullBrokerLifecycleLog Instance = new();

    private NullBrokerLifecycleLog()
    {
    }

    public void Write(BrokerLifecycleLogEvent lifecycleEvent)
    {
    }
}

public sealed class JsonlBrokerLifecycleLog : IBrokerLifecycleLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxRollingFiles = 3;
    private const int MaxMessageLength = 300;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string _directory;

    public JsonlBrokerLifecycleLog(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public void Write(BrokerLifecycleLogEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        try
        {
            var normalized = lifecycleEvent with
            {
                Message = Sanitize(lifecycleEvent.Message)
            };
            var line = JsonSerializer.Serialize(normalized, JsonOptions) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                VaultFileSecurity.ProtectDirectory(_directory);
                var prefix = $"broker-{normalized.OccurredAt:yyyyMMdd}";
                var currentPath = Path.Combine(_directory, prefix + ".jsonl");
                RollIfNeeded(currentPath, prefix, bytes.Length);
                using (var stream = new FileStream(
                           currentPath,
                           FileMode.Append,
                           FileAccess.Write,
                           FileShare.Read,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush();
                }
                VaultFileSecurity.ProtectFile(currentPath);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                $"Broker lifecycle log write failed: {exception.GetType().Name}.");
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

    private void RollIfNeeded(
        string currentPath,
        string prefix,
        int incomingBytes)
    {
        var currentLength = File.Exists(currentPath)
            ? new FileInfo(currentPath).Length
            : 0;
        if (currentLength == 0 ||
            currentLength + incomingBytes <= MaxFileBytes)
        {
            return;
        }

        for (var index = MaxRollingFiles; index >= 1; index--)
        {
            var source = index == 1
                ? currentPath
                : Path.Combine(_directory, $"{prefix}.{index - 1}.jsonl");
            var destination = Path.Combine(
                _directory,
                $"{prefix}.{index}.jsonl");
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }
    }
}
