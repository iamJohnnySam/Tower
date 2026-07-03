namespace Tower.Core.Secrets;

// Holds the derived key in memory for an unlocked Blazor circuit. Scoped => one per
// circuit; a hard refresh / reconnect drops it and re-prompts. Never persisted.
public class VaultSession
{
    private byte[]? _key;

    public bool Unlocked => _key is not null;

    public byte[] Key => _key ?? throw new InvalidOperationException("Vault is locked.");

    public void Unlock(byte[] key) => _key = key;

    public void Lock()
    {
        if (_key is not null) Array.Clear(_key);
        _key = null;
    }
}
