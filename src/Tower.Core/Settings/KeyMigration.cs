using System.Text.Json;
namespace Tower.Core.Settings;
public static class KeyMigration {
    public static Dictionary<string,string> FromServerMonitorConfig(string json) {
        var r = new Dictionary<string,string>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        void Take(string a, string b, string key) {
            if (root.TryGetProperty(a, out var o) && o.TryGetProperty(b, out var v) && v.ValueKind == JsonValueKind.String)
                r[key] = v.GetString()!;
        }
        Take("jellyfin","api_key","jellyfin.api_key");
        Take("dropbox","access_token","dropbox.access_token");
        Take("backup","schedule","backup.schedule");
        return r;
    }
}
