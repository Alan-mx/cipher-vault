using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Tests;

internal sealed class CapturingLoggerProvider :
    ILoggerProvider,
    ILogger
{
    private readonly ConcurrentQueue<string> messages = new();

    internal IReadOnlyList<string> Messages => messages.ToArray();

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        messages.Enqueue(formatter(state, exception));
    }

    public void Dispose()
    {
    }
}
