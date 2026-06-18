using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Tower.Core.Pi;

public class PiAgentClient(HttpClient http)
{
    static CancellationToken Timeout(int sec = 4) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(sec)).Token;

    public async Task<JsonNode?> StatsAsync(string baseUrl)
    {
        try
        {
            var json = await http.GetStringAsync($"{baseUrl}/api/stats", Timeout());
            return JsonNode.Parse(json);
        }
        catch { return null; }
    }

    public async Task<JsonNode?> ConfigAsync(string baseUrl)
    {
        try
        {
            var json = await http.GetStringAsync($"{baseUrl}/api/config", Timeout());
            return JsonNode.Parse(json);
        }
        catch { return null; }
    }

    public async Task<bool> SetConfigAsync(string baseUrl, JsonNode body)
    {
        try
        {
            var r = await http.PostAsJsonAsync($"{baseUrl}/api/config", body, Timeout());
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> ShutdownAsync(string baseUrl)
    {
        try
        {
            var r = await http.PostAsync($"{baseUrl}/api/shutdown", null, Timeout());
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> IdleResetAsync(string baseUrl)
    {
        try
        {
            var r = await http.PostAsync($"{baseUrl}/api/idle/reset", null, Timeout());
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<string?> LogsAsync(string baseUrl)
    {
        try
        {
            return await http.GetStringAsync($"{baseUrl}/api/logs", Timeout());
        }
        catch { return null; }
    }

    public async Task<JsonNode?> JellyfinSessionsAsync(string baseUrl)
    {
        try
        {
            var json = await http.GetStringAsync($"{baseUrl}/api/jellyfin/sessions", Timeout());
            return JsonNode.Parse(json);
        }
        catch { return null; }
    }
}
