using System.Security.Cryptography;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Crypto;

public sealed record VaultCreationResult(
    VaultKeyEnvelope Envelope,
    VaultKeyMaterial KeyMaterial);

public sealed record PasswordChangeResult(
    VaultKeyEnvelope Envelope,
    VaultKeyMaterial KeyMaterial);

public static class VaultKeyService
{
    public static VaultCreationResult Create(string password, Guid vaultId)
    {
        PasswordPolicy.Validate(password);
        if (vaultId == Guid.Empty)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Vault ID must not be empty.");
        }

        var salt = RandomNumberGenerator.GetBytes(VaultKdfParameters.SaltBytes);
        var parameters = VaultKdfParameters.CreateDefault(salt);
        var masterKey = RandomNumberGenerator.GetBytes(MasterKeyProtector.MasterKeyBytes);
        var kek = Argon2PasswordDeriver.DeriveKek(password, parameters);
        try
        {
            var envelope = new VaultKeyEnvelope(
                VaultKeyEnvelope.ExpectedFormat,
                VaultKeyEnvelope.CurrentVersion,
                vaultId,
                parameters,
                MasterKeyProtector.Wrap(masterKey, kek, vaultId, parameters));
            envelope.Validate();
            var databaseKey = VaultKeyDeriver.DeriveDatabaseKey(vaultId, masterKey);
            return new VaultCreationResult(
                envelope,
                new VaultKeyMaterial(vaultId, masterKey, databaseKey));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static async Task<VaultKeyMaterial> UnlockAsync(
        string keyPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        PasswordPolicy.ValidateAuthenticationAttempt(password);
        var envelope = await VaultKeyEnvelopeSerializer.LoadAsync(keyPath, cancellationToken);
        var kek = Argon2PasswordDeriver.DeriveKek(password, envelope.Kdf, cancellationToken);
        byte[]? masterKey = null;
        try
        {
            masterKey = MasterKeyProtector.Unwrap(envelope, kek);
            var databaseKey = VaultKeyDeriver.DeriveDatabaseKey(envelope.VaultId, masterKey);
            return new VaultKeyMaterial(envelope.VaultId, masterKey, databaseKey);
        }
        catch
        {
            if (masterKey is not null)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static Task<PasswordChangeResult> ChangePasswordAsync(
        string keyPath,
        string oldPassword,
        string newPassword,
        CancellationToken cancellationToken = default) =>
        ChangePasswordCoreAsync(
            keyPath,
            oldPassword,
            newPassword,
            (envelope, ct) => VaultKeyEnvelopeSerializer.WriteAtomicAsync(keyPath, envelope, ct),
            cancellationToken);

    internal static async Task<PasswordChangeResult> ChangePasswordCoreAsync(
        string keyPath,
        string oldPassword,
        string newPassword,
        Func<VaultKeyEnvelope, CancellationToken, Task> writeEnvelope,
        CancellationToken cancellationToken)
    {
        PasswordPolicy.ValidateAuthenticationAttempt(oldPassword);
        PasswordPolicy.Validate(newPassword);
        var currentEnvelope = await VaultKeyEnvelopeSerializer.LoadAsync(keyPath, cancellationToken);
        var oldKek = Argon2PasswordDeriver.DeriveKek(oldPassword, currentEnvelope.Kdf, cancellationToken);
        byte[]? masterKey = null;
        byte[]? newKek = null;
        try
        {
            masterKey = MasterKeyProtector.Unwrap(currentEnvelope, oldKek);
            var newSalt = RandomNumberGenerator.GetBytes(VaultKdfParameters.SaltBytes);
            var newParameters = VaultKdfParameters.CreateDefault(newSalt);
            newKek = Argon2PasswordDeriver.DeriveKek(newPassword, newParameters, cancellationToken);
            var databaseKey = VaultKeyDeriver.DeriveDatabaseKey(currentEnvelope.VaultId, masterKey);
            var newEnvelope = new VaultKeyEnvelope(
                VaultKeyEnvelope.ExpectedFormat,
                VaultKeyEnvelope.CurrentVersion,
                currentEnvelope.VaultId,
                newParameters,
                MasterKeyProtector.Wrap(masterKey, newKek, currentEnvelope.VaultId, newParameters));
            try
            {
                await writeEnvelope(newEnvelope, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "Unable to replace the Vault key envelope.",
                    exception);
            }

            var result = new PasswordChangeResult(
                newEnvelope,
                new VaultKeyMaterial(currentEnvelope.VaultId, masterKey, databaseKey));
            masterKey = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKek);
            if (newKek is not null)
            {
                CryptographicOperations.ZeroMemory(newKek);
            }

            if (masterKey is not null)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
    }
}
