using System.Text.Json.Serialization;

namespace CipherVault.Core.Crypto;

public sealed record VaultKeyEnvelope
{
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("format_version")]
    public required int FormatVersion { get; init; }

    [JsonPropertyName("vault_id")]
    public required Guid VaultId { get; init; }

    [JsonPropertyName("key_generation")]
    public required int KeyGeneration { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("password_slot")]
    public required VaultKeySlot PasswordSlot { get; init; }

    [JsonPropertyName("recovery_slot")]
    public required VaultKeySlot RecoverySlot { get; init; }
}

public sealed record VaultKeySlot
{
    [JsonPropertyName("kdf")]
    public required string Kdf { get; init; }

    [JsonPropertyName("salt_b64")]
    public required string SaltB64 { get; init; }

    [JsonPropertyName("m_cost_kib")]
    public int? MemoryCostKiB { get; init; }

    [JsonPropertyName("t_cost")]
    public int? Iterations { get; init; }

    [JsonPropertyName("p_cost")]
    public int? Parallelism { get; init; }

    [JsonPropertyName("nonce_b64")]
    public required string NonceB64 { get; init; }

    [JsonPropertyName("wrapped_master_key_b64")]
    public required string WrappedMasterKeyB64 { get; init; }
}

public sealed record KeyCreationResult(
    VaultKeyEnvelope Envelope,
    string RecoveryKey,
    byte[] MasterKey,
    byte[] DbKey);

public sealed record UnlockedKeyMaterial(
    Guid VaultId,
    byte[] MasterKey,
    byte[] DbKey);
