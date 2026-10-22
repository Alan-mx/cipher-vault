namespace CipherVault.Host.Session;

public static class StrictPipeAcceptLoop
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> acceptOnce,
        TimeSpan failureBackoff,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(acceptOnce);
        if (failureBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(failureBackoff));
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await acceptOnce(stoppingToken).ConfigureAwait(false);
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
                    await Task.Delay(failureBackoff, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
