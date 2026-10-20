using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace CipherVault.Core.Session;

internal sealed class VaultSession : IAsyncDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan HardLifetime = TimeSpan.FromHours(4);

    public Guid SessionId { get; } = Guid.NewGuid();
    public required string VaultPath { get; init; }
    public required Guid VaultId { get; init; }
    public required SqliteConnection Connection { get; init; }
    public required byte[] MasterKey { get; init; }
    public required byte[] DbKey { get; init; }
    public required byte[] IntegrityKey { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastUserActivityAt { get; set; }
    public required DateTimeOffset HardExpiresAt { get; init; }
    public required bool RecoveryVerified { get; set; }
    public VaultSessionState State { get; set; } = VaultSessionState.Unlocked;

    public ValueTask DisposeAsync()
    {
        State = VaultSessionState.Locking;
        try
        {
            Connection.Dispose();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MasterKey);
            CryptographicOperations.ZeroMemory(DbKey);
            CryptographicOperations.ZeroMemory(IntegrityKey);
            State = VaultSessionState.Locked;
        }
        return ValueTask.CompletedTask;
    }
}
