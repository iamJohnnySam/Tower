using Tower.Core.Settings;
using Xunit;
namespace Tower.Core.Tests;
public class KeyMigrationTests {
    [Fact] public void Extracts_keys_from_servermonitor_config() {
        var json = """{"dropbox":{"access_token":"DTOK"},"backup":{"schedule":"02:00"},"jellyfin":{"api_key":"JKEY"}}""";
        var kv = KeyMigration.FromServerMonitorConfig(json);
        Assert.Equal("JKEY", kv["jellyfin.api_key"]);
        Assert.Equal("DTOK", kv["dropbox.access_token"]);
        Assert.Equal("02:00", kv["backup.schedule"]);
    }
    [Fact] public void Missing_sections_are_skipped_without_throwing() {
        var json = """{"jellyfin":{"api_key":"JKEY"}}""";
        var kv = KeyMigration.FromServerMonitorConfig(json);
        Assert.Equal("JKEY", kv["jellyfin.api_key"]);
        Assert.False(kv.ContainsKey("dropbox.access_token"));
        Assert.False(kv.ContainsKey("backup.schedule"));
    }
    [Fact] public void Empty_object_returns_empty_dict() {
        var kv = KeyMigration.FromServerMonitorConfig("{}");
        Assert.Empty(kv);
    }
}
