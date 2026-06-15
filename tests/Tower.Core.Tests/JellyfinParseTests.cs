using Tower.Core.Jellyfin;
namespace Tower.Core.Tests;
public class JellyfinParseTests {
    [Fact] public void Malformed_json_returns_empty() => Assert.Empty(JellyfinClient.ParseSessions("not json{"));
    [Fact] public void Object_root_returns_empty() => Assert.Empty(JellyfinClient.ParseSessions("{\"error\":\"Unauthorized\"}"));
    [Fact] public void Empty_array_returns_empty() => Assert.Empty(JellyfinClient.ParseSessions("[]"));
    [Fact] public void Parses_playing_and_idle_sessions() {
        var json = System.IO.File.ReadAllText("Fixtures/jellyfin_sessions.json");
        var sessions = JellyfinClient.ParseSessions(json);
        Assert.Equal(2, sessions.Count);
        var s1 = sessions[0];
        Assert.True(s1.Playing);
        Assert.Equal("Blade Runner 2049", s1.Media);
        Assert.Equal("Transcode", s1.Method);
        Assert.Contains("VideoCodecNotSupported", s1.TranscodeReasons);
        Assert.False(sessions[1].Playing);
    }
}
