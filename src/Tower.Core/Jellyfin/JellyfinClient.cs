using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
namespace Tower.Core.Jellyfin;
public class JellyfinClient(HttpClient http, ILogger<JellyfinClient>? logger = null) {
    public static List<SessionInfo> ParseSessions(string json, ILogger? logger = null) {
        var list = new List<SessionInfo>();
        try {
            var arr = JsonNode.Parse(json) as JsonArray;
            if (arr is null) return list;
            foreach (var n in arr) {
                if (n is null) continue;
                var np = n["NowPlayingItem"];
                var ps = n["PlayState"];
                var ti = n["TranscodingInfo"];
                bool playing = np is not null && np.GetValueKind() != JsonValueKind.Null;
                string S(JsonNode? o, string k) => o?[k]?.ToString() ?? "";
                int? I(JsonNode? o, string k) { try { return o?[k]?.GetValue<int>(); } catch { return null; } }
                var reasons = new List<string>();
                if (ti?["TranscodeReasons"] is JsonArray ra) foreach (var r in ra) if (r is not null) reasons.Add(r.ToString());
                long bitrate = 0; try { bitrate = ti?["Bitrate"]?.GetValue<long>() ?? 0; } catch { }
                int? videoBitDepth = null;
                if (playing && np?["MediaStreams"] is JsonArray streams)
                    foreach (var s in streams)
                        if (s?["Type"]?.ToString() == "Video") { try { videoBitDepth = s["BitDepth"]?.GetValue<int>(); } catch { } break; }
                list.Add(new SessionInfo(
                    SessionId: S(n,"Id"), User: S(n,"UserName"), Client: S(n,"Client"), Device: S(n,"DeviceName"),
                    Playing: playing,
                    MediaId: playing ? S(np,"Id") : "", Media: playing ? S(np,"Name") : "",
                    MediaType: playing ? S(np,"Type") : "", SeriesName: playing ? S(np,"SeriesName") : "",
                    SeasonNumber: playing ? I(np,"ParentIndexNumber") : null,
                    EpisodeNumber: playing ? I(np,"IndexNumber") : null,
                    Container: playing ? S(np,"Container") : "",
                    Method: playing ? S(ps,"PlayMethod") : "",
                    VideoCodec: S(ti,"VideoCodec"), AudioCodec: S(ti,"AudioCodec"),
                    TranscodeReasons: reasons, Bitrate: bitrate,
                    VideoBitDepth: videoBitDepth));
            }
        } catch (Exception ex) { logger?.LogWarning(ex, "Failed to parse Jellyfin sessions JSON"); return new List<SessionInfo>(); }
        return list;
    }
    public static string? ParseItemPath(string json, ILogger? logger = null)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?["Items"]?[0]?["Path"]?.ToString();
        }
        catch (Exception ex) { logger?.LogWarning(ex, "Failed to parse Jellyfin item path JSON"); return null; }
    }
    public async Task<string?> GetItemPathAsync(string baseUrl, string apiKey, string mediaId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var json = await http.GetStringAsync(
                $"{baseUrl}/Items?ids={mediaId}&api_key={apiKey}&Fields=Path", cts.Token);
            return ParseItemPath(json, logger);
        }
        catch (Exception ex) { logger?.LogWarning(ex, "Jellyfin item-path lookup failed for media {MediaId}", mediaId); return null; }
    }
    public async Task<List<SessionInfo>?> SessionsAsync(string baseUrl, string apiKey) {
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var json = await http.GetStringAsync($"{baseUrl}/Sessions?api_key={apiKey}", cts.Token);
            return ParseSessions(json, logger);
        } catch (Exception ex) { logger?.LogWarning(ex, "Jellyfin sessions fetch failed"); return null; }
    }
}
