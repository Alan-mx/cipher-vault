using System.IO;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Security;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

public sealed class JsonlSecurityEventLog : ISecurityEventLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxRollingFiles = 3;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;

    public JsonlSecurityEventLog(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CipherVault",
            "logs"));
    }

    public async Task WriteAsync(
        HostSecurityLogEvent securityEvent,
        CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(securityEvent, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            VaultFileSecurity.ProtectDirectory(_directory);
            var prefix = $"security-{securityEvent.OccurredAt:yyyyMMdd}";
            var currentPath = Path.Combine(_directory, prefix + ".jsonl");
            RollIfNeeded(currentPath, prefix, bytes.Length);
            await using var stream = new FileStream(
                currentPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            VaultFileSecurity.ProtectFile(currentPath);
        }
        finally
        {
            _gate.Release();
        }
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
