using System.IO.Pipes;

namespace CipherVault.Host.Session;

internal sealed class BridgeConnectionLifetime : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _disconnected = new();
    private readonly CancellationTokenSource _linked;
    private Task? _monitorTask;
    private bool _disposed;

    public BridgeConnectionLifetime(NamedPipeServerStream pipe)
    {
        _pipe = pipe;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(
            _disconnected.Token);
        Token = _linked.Token;
    }

    public CancellationToken Token { get; }

    public void StartMonitoring(CancellationToken stoppingToken)
    {
        if (_monitorTask is not null)
        {
            return;
        }
        _monitorTask = Task.Run(() => MonitorAsync(stoppingToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _disconnected.Cancel();
        _linked.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        _linked.Dispose();
        _disconnected.Dispose();
    }

    private async Task MonitorAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested &&
                !_disconnected.IsCancellationRequested)
            {
                if (!_pipe.IsConnected)
                {
                    _disconnected.Cancel();
                    return;
                }
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            stoppingToken.IsCancellationRequested)
        {
            _disconnected.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _disconnected.Cancel();
        }
    }
}
