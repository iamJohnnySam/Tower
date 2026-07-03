using System.Security.Cryptography;
using Tower.Core.Secrets;
using Xunit;

namespace Tower.Core.Tests;

public class VaultCryptoTests
{
    [Fact]
    public void Roundtrip_returns_original_plaintext()
    {
        var key = VaultCrypto.DeriveKey("hunter2", VaultCrypto.NewSalt());
        var blob = VaultCrypto.Encrypt(key, "designworks_dev");
        Assert.Equal("designworks_dev", VaultCrypto.Decrypt(key, blob));
    }

    [Fact]
    public void Wrong_password_fails_to_decrypt()
    {
        var salt = VaultCrypto.NewSalt();
        var good = VaultCrypto.DeriveKey("correct", salt);
        var bad  = VaultCrypto.DeriveKey("wrong",   salt);
        var blob = VaultCrypto.Encrypt(good, "secret");
        Assert.Throws<AuthenticationTagMismatchException>(() => VaultCrypto.Decrypt(bad, blob));
    }

    [Fact]
    public void Verifier_matches_only_for_the_right_key()
    {
        var salt = VaultCrypto.NewSalt();
        var key  = VaultCrypto.DeriveKey("pw", salt);
        var verifier = VaultCrypto.Verifier(key);

        Assert.True(VaultCrypto.KeyMatches(VaultCrypto.DeriveKey("pw", salt), verifier));
        Assert.False(VaultCrypto.KeyMatches(VaultCrypto.DeriveKey("nope", salt), verifier));
    }

    [Fact]
    public void Each_encryption_uses_a_fresh_nonce()
    {
        var key = VaultCrypto.DeriveKey("pw", VaultCrypto.NewSalt());
        Assert.NotEqual(VaultCrypto.Encrypt(key, "same"), VaultCrypto.Encrypt(key, "same"));
    }
}
