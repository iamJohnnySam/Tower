using System.Text.Json.Nodes;

namespace Tower.Core.PiHole;

public record SummaryStats(
    long QueriesToday,
    long BlockedToday,
    double BlockedPercent,
    long UniqueClients,
    long GravityDomains);

public record BlockedDomain(string Domain, int Hits);

public record PiHoleData(
    SummaryStats Stats,
    bool BlockingEnabled,
    List<BlockedDomain> TopBlockedDomains,
    List<string> UpstreamServers,
    string? Error,
    DateTime FetchedAt);

public static class PiHoleParser
{
    public static SummaryStats? ParseSummary(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var q = root?["queries"];
            var g = root?["gravity"];
            var c = root?["clients"];
            if (q is null) return null;
            return new SummaryStats(
                QueriesToday:    q["total"]?.GetValue<long>()             ?? 0,
                BlockedToday:    q["blocked"]?.GetValue<long>()           ?? 0,
                BlockedPercent:  q["percent_blocked"]?.GetValue<double>() ?? 0,
                UniqueClients:   c?["active"]?.GetValue<long>()           ?? 0,
                GravityDomains:  g?["domains_being_blocked"]?.GetValue<long>() ?? 0);
        }
        catch { return null; }
    }

    public static bool? ParseBlocking(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var val = root?["blocking"]?.ToString();
            if (val is null) return null;
            // PiHole v6 returns "enabled"/"disabled" string
            if (val == "enabled")  return true;
            if (val == "disabled") return false;
            return bool.TryParse(val, out var b) ? b : null;
        }
        catch { return null; }
    }

    public static List<BlockedDomain> ParseTopDomains(string json)
    {
        var list = new List<BlockedDomain>();
        try
        {
            var root = JsonNode.Parse(json);
            // PiHole v6 uses "domains" key (not "top_domains")
            var arr = root?["domains"] as JsonArray ?? root?["top_domains"] as JsonArray;
            if (arr is null) return list;
            foreach (var n in arr)
            {
                var domain = n?["domain"]?.ToString();
                var count  = n?["count"]?.GetValue<int>() ?? 0;
                if (!string.IsNullOrEmpty(domain))
                    list.Add(new BlockedDomain(domain, count));
            }
        }
        catch { }
        return list;
    }

    public static List<string> ParseUpstreams(string json)
    {
        var list = new List<string>();
        try
        {
            var root = JsonNode.Parse(json);
            // PiHole v6: { "config": { "dns": { "upstreams": [...] } } }
            if (root?["config"]?["dns"]?["upstreams"] is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    var s = n?.ToString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
        }
        catch { }
        return list;
    }
}
