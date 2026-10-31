using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Crypto;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class Argon2PasswordDeriverTests
{
    private const string ValidPassword = "correct horse battery staple";

    [Fact]
    public void DefaultParametersMatchFrozenValues()
    {
        var salt = RandomNumberGenerator.GetBytes(VaultKdfParameters.SaltBytes);

        var parameters = VaultKdfParameters.CreateDefault(salt);

        Assert.Equal("argon2id", parameters.Algorithm);
        Assert.Equal(65536, parameters.MemoryKiB);
        Assert.Equal(3, parameters.Iterations);
        Assert.Equal(1, parameters.Parallelism);
        Assert.Equal(salt, parameters.Salt);
    }

    [Fact]
    public void PasswordMinimumCountsUnicodeScalars()
    {
        PasswordPolicy.Validate(string.Concat(Enumerable.Repeat("😀", 12)));

        var exception = Assert.Throws<VaultException>(() =>
            PasswordPolicy.Validate(string.Concat(Enumerable.Repeat("😀", 11))));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void InvalidSurrogatePairsAreRejected(int invalidCodeUnit)
    {
        var password = new string((char)invalidCodeUnit, 12);

        var exception = Assert.Throws<VaultException>(() => PasswordPolicy.Validate(password));
        var deriverException = Assert.Throws<VaultException>(() =>
            Argon2PasswordDeriver.DeriveKek(
                password,
                FastParameters(Encoding.UTF8.GetBytes("0123456789abcdef"))));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
        Assert.Equal(VaultErrorCode.InvalidInput, deriverException.Code);
    }

    [Fact]
    public void SamePasswordAndSaltProduceSameKek()
    {
        var parameters = FastParameters(Encoding.UTF8.GetBytes("0123456789abcdef"));

        var first = Argon2PasswordDeriver.DeriveKek(ValidPassword, parameters);
        var second = Argon2PasswordDeriver.DeriveKek(ValidPassword, parameters);

        try
        {
            Assert.Equal(32, first.Length);
            Assert.Equal(first, second);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    [Fact]
    public void DifferentSaltProducesDifferentKek()
    {
        var first = Argon2PasswordDeriver.DeriveKek(
            ValidPassword,
            FastParameters(Encoding.UTF8.GetBytes("0123456789abcdef")));
        var second = Argon2PasswordDeriver.DeriveKek(
            ValidPassword,
            FastParameters(Encoding.UTF8.GetBytes("fedcba9876543210")));

        try
        {
            Assert.NotEqual(first, second);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    [Fact]
    public void PasswordIsNotUnicodeNormalized()
    {
        var salt = Encoding.UTF8.GetBytes("0123456789abcdef");
        var composed = string.Concat(Enumerable.Repeat("\u00e9", 12));
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 12));

        var composedKek = Argon2PasswordDeriver.DeriveKek(composed, FastParameters(salt));
        var decomposedKek = Argon2PasswordDeriver.DeriveKek(decomposed, FastParameters(salt));

        try
        {
            Assert.NotEqual(composedKek, decomposedKek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(composedKek);
            CryptographicOperations.ZeroMemory(decomposedKek);
        }
    }

    [Theory]
    [InlineData(8192, 3, 1)]
    [InlineData(262145, 3, 1)]
    [InlineData(65536, 0, 1)]
    [InlineData(65536, 11, 1)]
    [InlineData(65536, 3, 0)]
    [InlineData(65536, 3, 5)]
    public void OutOfRangeParametersAreRejected(int memoryKiB, int iterations, int parallelism)
    {
        var parameters = new VaultKdfParameters(
            "argon2id",
            memoryKiB,
            iterations,
            parallelism,
            new byte[16]);

        var exception = Assert.Throws<VaultException>(parameters.Validate);

        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
    }

    private static VaultKdfParameters FastParameters(byte[] salt) =>
        new("argon2id", 16384, 1, 1, salt);
}
