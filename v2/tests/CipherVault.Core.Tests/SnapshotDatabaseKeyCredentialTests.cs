using CipherVault.Core.Backup;

namespace CipherVault.Core.Tests;

public sealed class SnapshotDatabaseKeyCredentialTests
{
    [Fact]
    public void DisposeClearsViewAndInvokesCallbackOnce()
    {
        var callbackCount = 0;
        var credential = new SnapshotDatabaseKeyCredential(
            new byte[32],
            () => callbackCount++);
        var escapedView = credential.DatabaseKey;

        Assert.Equal(32, escapedView.Length);
        credential.Dispose();
        credential.Dispose();

        Assert.Equal(1, callbackCount);
        Assert.Throws<ObjectDisposedException>(
            () => _ = credential.DatabaseKey);

        // Existing ReadOnlyMemory views cannot be revoked by Dispose.
        Assert.Equal(32, escapedView.Length);
    }
}
