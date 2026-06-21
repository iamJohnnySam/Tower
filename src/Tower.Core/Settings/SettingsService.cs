using Tower.Core.Data;
using Tower.Core.Models;
namespace Tower.Core.Settings;
public class SettingsService(TowerDbContext db) {
    private readonly Dictionary<string, string?> _cache = new();

    public string? Get(string key)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var value = db.Settings.Find(key)?.Value;
        _cache[key] = value;
        return value;
    }

    public bool IsConfigured(string key) => !string.IsNullOrWhiteSpace(Get(key));

    public void Set(string key, string? value) {
        var s = db.Settings.Find(key);
        if (s is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else s.Value = value;
        db.SaveChanges();
        _cache[key] = value;
    }
}
