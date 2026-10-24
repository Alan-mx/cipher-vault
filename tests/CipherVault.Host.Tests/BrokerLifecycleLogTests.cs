using System.Text.Json;
using CipherVault.Host.Operations;

namespace CipherVault.Host.Tests;

public sealed class BrokerLifecycleLogTests
{
    [Fact]
    public void WritesStructuredSingleLineEvent()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-broker-log-test-{Guid.NewGuid():N}");
        try
        {
            var occurredAt = DateTimeOffset.Parse("2026-10-24T10:00:00Z");
            var log = new JsonlBrokerLifecycleLog(directory);

            log.Write(new BrokerLifecycleLogEvent(
                occurredAt,
                "pipe_failed",
                "create",
                "error",
                ProcessId: 42,
                RetryCount: 3,
                ErrorType: "IOException",
                Win32ErrorCode: 5,
                Message: "first line\r\nsecond line"));

            var path = Assert.Single(
                Directory.GetFiles(directory, "broker-*.jsonl"));
            var line = Assert.Single(File.ReadAllLines(path));
            var entry = JsonSerializer.Deserialize<BrokerLifecycleLogEvent>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(entry);
            Assert.Equal(occurredAt, entry.OccurredAt);
            Assert.Equal("pipe_failed", entry.EventType);
            Assert.Equal(42u, entry.ProcessId);
            Assert.Equal(3, entry.RetryCount);
            Assert.Equal(5, entry.Win32ErrorCode);
            Assert.Equal("first line  second line", entry.Message);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
