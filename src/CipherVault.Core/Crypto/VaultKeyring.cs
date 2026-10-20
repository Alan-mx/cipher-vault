using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Core.Session;

namespace CipherVault.Core.Crypto;

public static class VaultKeyring
{
    public const int Argon2MemoryKiB = 64 * 1024;
    public const int Argon2Iterations = 3;
    public const int Argon2Parallelism = 1;
    public const int PasswordMinimumLength = 12;
    public const int MaxArgon2MemoryKiB = 256 * 1024;
    public const int MaxArgon2Iterations = 10;
    public const int MaxArgon2Parallelism = 16;
    internal const long MaxEnvelopeBytes = 1024 * 1024;
    private const string FormatName = "cipher-vault-keyring";
    private const int CurrentFormatVersion = 1;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int WrappedMasterKeyBytes = 48;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<KeyCreationResult> CreateAsync(
        string password,
        Guid vaultId,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var recoveryKey = RandomNumberGenerator.GetBytes(32);
        var passwordSalt = RandomNumberGenerator.GetBytes(16);
        var recoverySalt = RandomNumberGenerator.GetBytes(16);

        var passwordKek = DerivePasswordKey(password, passwordSalt);
        var recoveryKek = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            recoveryKey,
            32,
            recoverySalt,
            Encoding.UTF8.GetBytes("pv/recovery/v1"));

        var envelope = new VaultKeyEnvelope
        {
            Format = FormatName,
            FormatVersion = CurrentFormatVersion,
            VaultId = vaultId,
            KeyGeneration = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            PasswordSlot = Wrap(
                masterKey,
                passwordKek,
                vaultId,
                "password",
                passwordSalt,
                Argon2MemoryKiB,
                Argon2Iterations,
                Argon2Parallelism),
            RecoverySlot = Wrap(
                masterKey,
                recoveryKek,
                vaultId,
                "recovery",
                recoverySalt,
                null,
                null,
                null)
        };

        var dbKey = DeriveDbKey(masterKey);
        await Task.Yield();
        return new KeyCreationResult(
            envelope,
            RecoveryKeyCodec.Format(recoveryKey),
            masterKey,
            dbKey);
    }

    public static async Task<UnlockedKeyMaterial> UnwrapAsync(
        VaultKeyEnvelope envelope,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnvelope(envelope);
        try
        {
            var masterKey = credentialType switch
            {
                VaultCredentialType.Password => UnwrapPassword(envelope, credential),
                VaultCredentialType.RecoveryKey => UnwrapRecovery(envelope, credential),
                _ => throw new VaultException(VaultErrorCode.InvalidInput, "Unsupported credential type.")
            };

            var dbKey = DeriveDbKey(masterKey);
            await Task.Yield();
            return new UnlockedKeyMaterial(envelope.VaultId, masterKey, dbKey);
        }
        catch (CryptographicException exception)
        {
            throw new VaultException(
                VaultErrorCode.InvalidCredential,
                "Vault credential is invalid.",
                exception);
        }
    }

    public static async Task<VaultKeyEnvelope> LoadEnvelopeAsync(
        string keyPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fileLength = new FileInfo(keyPath).Length;
            if (fileLength <= 0 || fileLength > MaxEnvelopeBytes)
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "Vault key envelope size is invalid.");
            }
            await using var stream = File.OpenRead(keyPath);
            var envelope = await JsonSerializer.DeserializeAsync<VaultKeyEnvelope>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "Vault key envelope is empty.");
            }
            ValidateEnvelope(envelope);
            return envelope;
        }
        catch (FormatException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault key envelope is invalid.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault key envelope is invalid.",
                exception);
        }
    }

    public static async Task WriteEnvelopeAtomicAsync(
        string keyPath,
        VaultKeyEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new VaultException(VaultErrorCode.InvalidInput, "Key path has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = keyPath + ".tmp";

        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                envelope,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, keyPath, overwrite: true);
        VaultFileSecurity.ProtectFile(keyPath);
    }

    public static async Task<string?> WriteEnvelopeWithBackupAsync(
        string keyPath,
        VaultKeyEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        string? backupPath = null;
        if (File.Exists(keyPath))
        {
            backupPath = keyPath + ".bak";
            File.Copy(keyPath, backupPath, overwrite: true);
            VaultFileSecurity.ProtectFile(backupPath);
        }
        await WriteEnvelopeAtomicAsync(keyPath, envelope, cancellationToken)
            .ConfigureAwait(false);
        return backupPath;
    }

    public static VaultKeySlot CreatePasswordSlot(byte[] masterKey, string password, Guid vaultId)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = DerivePasswordKey(password, salt);
        return Wrap(
            masterKey,
            kek,
            vaultId,
            "password",
            salt,
            Argon2MemoryKiB,
            Argon2Iterations,
            Argon2Parallelism);
    }

    public static (string RecoveryKey, VaultKeySlot Slot) CreateRecoverySlot(
        byte[] masterKey,
        Guid vaultId)
    {
        var recoveryKey = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            recoveryKey,
            32,
            salt,
            Encoding.UTF8.GetBytes("pv/recovery/v1"));
        var slot = Wrap(
            masterKey,
            kek,
            vaultId,
            "recovery",
            salt,
            null,
            null,
            null);
        return (RecoveryKeyCodec.Format(recoveryKey), slot);
    }

    public static byte[] DeriveDbKey(byte[] masterKey)
    {
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            32,
            salt: null,
            info: Encoding.UTF8.GetBytes("pv/db/v1"));
    }

    public static byte[] DeriveIntegrityKey(byte[] masterKey)
    {
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            32,
            salt: null,
            info: Encoding.UTF8.GetBytes("pv/integrity/v1"));
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < PasswordMinimumLength)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Password must contain at least {PasswordMinimumLength} characters.");
        }
    }

    private static byte[] UnwrapPassword(VaultKeyEnvelope envelope, string password)
    {
        ValidatePassword(password);
        var slot = envelope.PasswordSlot;
        var kek = DerivePasswordKey(
            password,
            DecodeBase64(slot.SaltB64, "password salt"),
            slot.MemoryCostKiB.GetValueOrDefault(),
            slot.Iterations.GetValueOrDefault(),
            slot.Parallelism.GetValueOrDefault());
        return Unwrap(slot, kek, envelope.VaultId, "password");
    }

    private static byte[] UnwrapRecovery(VaultKeyEnvelope envelope, string recoveryKey)
    {
        var rawRecoveryKey = RecoveryKeyCodec.Parse(recoveryKey);
        var recoveryKek = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            rawRecoveryKey,
            32,
            DecodeBase64(envelope.RecoverySlot.SaltB64, "recovery salt"),
            Encoding.UTF8.GetBytes("pv/recovery/v1"));
        return Unwrap(envelope.RecoverySlot, recoveryKek, envelope.VaultId, "recovery");
    }

    private static byte[] DerivePasswordKey(
        string password,
        byte[] salt,
        int memoryKiB = Argon2MemoryKiB,
        int iterations = Argon2Iterations,
        int parallelism = Argon2Parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKiB
        };
        return argon2.GetBytes(32);
    }

    private static VaultKeySlot Wrap(
        byte[] masterKey,
        byte[] kek,
        Guid vaultId,
        string slotKind,
        byte[] salt,
        int? memoryKiB,
        int? iterations,
        int? parallelism)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[masterKey.Length];
        var tag = new byte[16];
        var aad = Encoding.UTF8.GetBytes($"cipher-vault-keyring-v1|{vaultId}|{slotKind}");
        using (var aes = new AesGcm(kek, 16))
        {
            aes.Encrypt(nonce, masterKey, ciphertext, tag, aad);
        }

        var wrapped = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, wrapped, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, wrapped, ciphertext.Length, tag.Length);
        return new VaultKeySlot
        {
            Kdf = slotKind == "password" ? "argon2id" : "hkdf-sha256",
            SaltB64 = Convert.ToBase64String(salt),
            MemoryCostKiB = memoryKiB,
            Iterations = iterations,
            Parallelism = parallelism,
            NonceB64 = Convert.ToBase64String(nonce),
            WrappedMasterKeyB64 = Convert.ToBase64String(wrapped)
        };
    }

    private static byte[] Unwrap(
        VaultKeySlot slot,
        byte[] kek,
        Guid vaultId,
        string slotKind)
    {
        var nonce = DecodeBase64(slot.NonceB64, "key slot nonce");
        var wrapped = DecodeBase64(slot.WrappedMasterKeyB64, "wrapped master key");

        var ciphertext = wrapped[..32];
        var tag = wrapped[32..];
        var masterKey = new byte[32];
        var aad = Encoding.UTF8.GetBytes($"cipher-vault-keyring-v1|{vaultId}|{slotKind}");
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(nonce, ciphertext, tag, masterKey, aad);
        return masterKey;
    }

    private static void ValidateEnvelope(VaultKeyEnvelope envelope)
    {
        if (envelope is null ||
            !string.Equals(envelope.Format, FormatName, StringComparison.Ordinal) ||
            envelope.FormatVersion != CurrentFormatVersion ||
            envelope.VaultId == Guid.Empty ||
            envelope.KeyGeneration < 1 ||
            envelope.PasswordSlot is null ||
            envelope.RecoverySlot is null)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Vault key envelope format is unsupported.");
        }

        ValidateSlot(
            envelope.PasswordSlot,
            "argon2id",
            requireArgon2: true,
            "password");
        ValidateSlot(
            envelope.RecoverySlot,
            "hkdf-sha256",
            requireArgon2: false,
            "recovery");
    }

    private static void ValidateSlot(
        VaultKeySlot slot,
        string expectedKdf,
        bool requireArgon2,
        string slotName)
    {
        if (slot is null ||
            !string.Equals(slot.Kdf, expectedKdf, StringComparison.Ordinal))
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                $"{slotName} key slot KDF is unsupported.");
        }

        if (requireArgon2)
        {
            if (slot.MemoryCostKiB is null ||
                slot.Iterations is null ||
                slot.Parallelism is null ||
                slot.MemoryCostKiB < Argon2MemoryKiB ||
                slot.MemoryCostKiB > MaxArgon2MemoryKiB ||
                slot.Iterations < Argon2Iterations ||
                slot.Iterations > MaxArgon2Iterations ||
                slot.Parallelism < Argon2Parallelism ||
                slot.Parallelism > MaxArgon2Parallelism)
            {
                throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    $"{slotName} KDF parameters are invalid.");
            }
        }
        else if (slot.MemoryCostKiB is not null ||
                 slot.Iterations is not null ||
                 slot.Parallelism is not null)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                $"{slotName} key slot KDF parameters are invalid.");
        }

        if (string.IsNullOrWhiteSpace(slot.SaltB64) ||
            string.IsNullOrWhiteSpace(slot.NonceB64) ||
            string.IsNullOrWhiteSpace(slot.WrappedMasterKeyB64) ||
            DecodeBase64(slot.SaltB64, $"{slotName} salt").Length != SaltBytes ||
            DecodeBase64(slot.NonceB64, $"{slotName} nonce").Length != NonceBytes ||
            DecodeBase64(
                slot.WrappedMasterKeyB64,
                $"{slotName} wrapped master key").Length != WrappedMasterKeyBytes)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                $"{slotName} key slot is invalid.");
        }
    }

    private static byte[] DecodeBase64(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                $"Vault keyring {fieldName} is invalid.");
        }
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                $"Vault keyring {fieldName} is invalid.",
                exception);
        }
    }
}
