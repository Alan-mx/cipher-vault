namespace CipherVault.Host.Session;

public sealed class BridgePipeAcceptLoop(BridgePipeServerOptions options)
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMilliseconds(100);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var loopOptions = options ?? throw new ArgumentNullException(nameof(options));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new BridgePipeServer(loopOptions);
                await server.AcceptOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(FailureBackoff, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }
}
