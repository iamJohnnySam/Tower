using Tower.Core.Data;
using Tower.Core.Models;
namespace Tower.Core.Settings;
public class SettingsService(TowerDbContext db) {
    public string? Get(string key) => db.Settings.Find(key)?.Value;
    public bool IsConfigured(string key) => !string.IsNullOrWhiteSpace(Get(key));
    public void Set(string key, string? value) {
        var s = db.Settings.Find(key);
        if (s is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else s.Value = value;
        db.SaveChanges();
    }
}
