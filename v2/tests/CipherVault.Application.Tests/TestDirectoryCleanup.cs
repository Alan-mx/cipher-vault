namespace CipherVault.Application.Tests;

internal static class TestDirectoryCleanup
{
    internal static async Task DeleteBestEffortAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(50);
            }
            catch (IOException)
            {
                // LockFailed is a restart boundary; the OS releases native file
                // handles when the test host exits.
                return;
            }
        }
    }
}
