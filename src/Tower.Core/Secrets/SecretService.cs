using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.Settings;

namespace Tower.Core.Secrets;

// Project-scoped password store. Once a master password is set, Value is AES-GCM
// encrypted at rest (see VaultCrypto) and can only be read through an unlocked session.
public class SecretService(TowerDbContext db, SettingsService settings, VaultSession session)
{
    private const string SaltKey     = "vault.salt";
    private const string VerifierKey = "vault.verifier";

    public bool IsConfigured => settings.IsConfigured(SaltKey);
    public bool IsUnlocked   => session.Unlocked;

    // First-time setup: generate salt, derive key, encrypt any existing plaintext rows,
    // then unlock this session. Refuses if already configured.
    public async Task ConfigureAsync(string password)
    {
        if (IsConfigured) throw new InvalidOperationException("Vault already has a master password.");

        var salt = VaultCrypto.NewSalt();
        var key  = VaultCrypto.DeriveKey(password, salt);

        var rows = await db.Secrets.ToListAsync();
        foreach (var s in rows) s.Value = VaultCrypto.Encrypt(key, s.Value);
        await db.SaveChangesAsync();

        settings.Set(SaltKey, Convert.ToBase64String(salt));
        settings.Set(VerifierKey, VaultCrypto.Verifier(key));
        session.Unlock(key);
    }

    // Returns true and unlocks the session if the password is correct.
    public bool TryUnlock(string password)
    {
        var salt     = settings.Get(SaltKey);
        var verifier = settings.Get(VerifierKey);
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(verifier)) return false;

        var key = VaultCrypto.DeriveKey(password, Convert.FromBase64String(salt));
        if (!VaultCrypto.KeyMatches(key, verifier)) return false;

        session.Unlock(key);
        return true;
    }

    public void Lock() => session.Lock();

    // Decrypted view. Requires an unlocked session once configured. AsNoTracking so the
    // in-memory plaintext can never be flushed back over the ciphertext.
    public async Task<List<Secret>> AllAsync()
    {
        var rows = await db.Secrets.AsNoTracking()
            .OrderBy(s => s.Project).ThenBy(s => s.Label)
            .ToListAsync();

        if (IsConfigured)
        {
            foreach (var s in rows)
            {
                try { s.Value = VaultCrypto.Decrypt(session.Key, s.Value); }
                catch { s.Value = "(decrypt failed)"; }
            }
        }
        return rows;
    }

    public async Task<Secret> UpsertAsync(int id, string project, string label, string value, string? notes)
    {
        var s = id > 0 ? await db.Secrets.FindAsync(id) : null;
        if (s is null)
        {
            s = new Secret();
            db.Secrets.Add(s);
        }
        s.Project   = project.Trim();
        s.Label     = label.Trim();
        s.Value     = IsConfigured ? VaultCrypto.Encrypt(session.Key, value) : value;
        s.Notes     = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return s;
    }

    public async Task DeleteAsync(int id)
    {
        var s = await db.Secrets.FindAsync(id);
        if (s is null) return;
        db.Secrets.Remove(s);
        await db.SaveChangesAsync();
    }
}
