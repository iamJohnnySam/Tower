using System.Security.Cryptography;
using System.Text;

namespace Tower.Core.Secrets;

// AES-256-GCM at rest, key derived from the master password via PBKDF2-SHA256.
// The key is never stored — only a salt and a verifier (SHA256 of the key) live in
// the DB, so a stolen tower.db reveals nothing without the password.
public static class VaultCrypto
{
    // ponytail: 300k PBKDF2 iters — plenty for a single-user gate on this box
    // (~0.2s/unlock). Bump toward 600k (OWASP) if the box gets much faster.
    private const int Iterations = 300_000;
    private const int KeyBytes   = 32;   // AES-256
    private const int SaltBytes  = 16;
    private const int NonceBytes = 12;   // AES-GCM standard
    private const int TagBytes   = 16;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);

    // Stored to check the password on unlock without holding the key. SHA256(key) does
    // not reveal the key, so exposing the verifier is safe.
    public static string Verifier(byte[] key) => Convert.ToBase64String(SHA256.HashData(key));

    public static bool KeyMatches(byte[] key, string verifier) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(key), Convert.FromBase64String(verifier));

    // Output: base64( nonce(12) || ciphertext || tag(16) ). Random nonce per call.
    public static string Encrypt(byte[] key, string plaintext)
    {
        var pt    = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ct    = new byte[pt.Length];
        var tag   = new byte[TagBytes];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, pt, ct, tag);

        var blob = new byte[NonceBytes + ct.Length + TagBytes];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceBytes);
        Buffer.BlockCopy(ct,    0, blob, NonceBytes, ct.Length);
        Buffer.BlockCopy(tag,   0, blob, NonceBytes + ct.Length, TagBytes);
        return Convert.ToBase64String(blob);
    }

    // Throws CryptographicException on a wrong key / tampered blob (GCM tag mismatch).
    public static string Decrypt(byte[] key, string blobB64)
    {
        var blob  = Convert.FromBase64String(blobB64);
        var nonce = blob.AsSpan(0, NonceBytes);
        var tag   = blob.AsSpan(blob.Length - TagBytes, TagBytes);
        var ct    = blob.AsSpan(NonceBytes, blob.Length - NonceBytes - TagBytes);
        var pt    = new byte[ct.Length];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(nonce, ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }
}
