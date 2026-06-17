using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Tower.Core.PiHole;

public class PiHoleClient(HttpClient http)
{
    private const string BaseUrl = "http://localhost";

    public async Task<PiHoleData> FetchAsync(string password)
    {
        string? sid = null;
        try
        {
            // Authenticate
            sid = await AuthAsync(password);
            if (sid is null)
                return Error("Authentication failed — check PiHole password in Settings");

            using var handler = BuildHandler(sid);
            using var client  = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) };

            // Fetch all four endpoints in parallel
            var summaryTask  = GetStringAsync(client, "/api/stats/summary");
            var blockingTask = GetStringAsync(client, "/api/dns/blocking");
            var topTask      = GetStringAsync(client, "/api/stats/top_domains?blocked=true&count=10");
            var dnsTask      = GetStringAsync(client, "/api/config/dns");
            await Task.WhenAll(summaryTask, blockingTask, topTask, dnsTask);

            var stats    = summaryTask.Result  is string sj ? PiHoleParser.ParseSummary(sj)   : null;
            var blocking = blockingTask.Result is string bj ? PiHoleParser.ParseBlocking(bj)  : null;
            var top      = topTask.Result      is string tj ? PiHoleParser.ParseTopDomains(tj) : new List<BlockedDomain>();
            var ups      = dnsTask.Result      is string dj ? PiHoleParser.ParseUpstreams(dj)  : new List<string>();

            return new PiHoleData(
                Stats:             stats ?? new SummaryStats(0, 0, 0, 0, 0),
                BlockingEnabled:   blocking ?? false,
                TopBlockedDomains: top,
                UpstreamServers:   ups,
                Error:             null,
                FetchedAt:         DateTime.Now);
        }
        catch (HttpRequestException)
        {
            return Error("Could not reach PiHole at localhost");
        }
        catch (TaskCanceledException)
        {
            return Error("PiHole request timed out");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
        finally
        {
            if (sid is not null) await LogoutAsync(sid);
        }
    }

    private async Task<string?> AuthAsync(string password)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await http.PostAsJsonAsync($"{BaseUrl}/api/auth", new { password }, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            var root = JsonNode.Parse(body);
            var sid  = root?["session"]?["sid"]?.ToString();
            var valid = root?["session"]?["valid"]?.GetValue<bool>() ?? false;
            return valid && !string.IsNullOrEmpty(sid) ? sid : null;
        }
        catch { return null; }
    }

    private async Task LogoutAsync(string sid)
    {
        try
        {
            using var handler = BuildHandler(sid);
            using var client  = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) };
            using var cts     = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.DeleteAsync("/api/auth", cts.Token);
        }
        catch { }
    }

    private static HttpClientHandler BuildHandler(string sid)
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri(BaseUrl), new Cookie("SID", sid));
        return new HttpClientHandler { CookieContainer = cookies };
    }

    private static async Task<string?> GetStringAsync(HttpClient client, string path)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await client.GetStringAsync(path, cts.Token);
        }
        catch { return null; }
    }

    private static PiHoleData Error(string msg) => new(
        Stats:             new SummaryStats(0, 0, 0, 0, 0),
        BlockingEnabled:   false,
        TopBlockedDomains: [],
        UpstreamServers:   [],
        Error:             msg,
        FetchedAt:         DateTime.Now);
}
