namespace CipherVault.Host.Tests;

internal static class HostIntegrationTestLock
{
    private const string Name = @"Local\CipherVault.HostIntegrationTests";

    public static IDisposable Acquire()
    {
        var semaphore = new Semaphore(1, 1, Name);
        if (!semaphore.WaitOne(TimeSpan.FromSeconds(120)))
        {
            semaphore.Dispose();
            throw new TimeoutException("Timed out waiting for Host integration test lock.");
        }
        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private Semaphore? _semaphore;

        public Releaser(Semaphore semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            semaphore?.Release();
            semaphore?.Dispose();
        }
    }
}
