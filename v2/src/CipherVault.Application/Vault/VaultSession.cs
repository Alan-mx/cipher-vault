using CipherVault.Core.Crypto;
using CipherVault.Core.Database;

namespace CipherVault.Application.Vault;

internal sealed class VaultSession
{
    internal VaultSession(
        Guid sessionId,
        long generation,
        VaultDatabaseRuntime database,
        VaultKeyMaterial keyMaterial,
        DateTimeOffset startedAtUtc,
        VaultActivityState activity)
    {
        SessionId = sessionId;
        Generation = generation;
        Database = database;
        KeyMaterial = keyMaterial;
        StartedAtUtc = startedAtUtc;
        Activity = activity;
    }

    internal Guid SessionId { get; }

    internal long Generation { get; }

    internal Guid VaultId => Database.VaultId;

    internal VaultDatabaseRuntime Database { get; }

    internal VaultKeyMaterial KeyMaterial { get; }

    internal DateTimeOffset StartedAtUtc { get; }

    internal VaultActivityState Activity { get; }

    internal CancellationTokenSource OperationCancellation { get; } = new();

    internal async ValueTask ReleaseAsync()
    {
        await Database.DisposeAsync();
        KeyMaterial.Dispose();
        OperationCancellation.Dispose();
    }
}
