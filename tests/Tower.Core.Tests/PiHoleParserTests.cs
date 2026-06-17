using Tower.Core.PiHole;

namespace Tower.Core.Tests;

public class PiHoleParserTests
{
    // ── ParseSummary ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseSummary_returns_null_on_malformed_json()
        => Assert.Null(PiHoleParser.ParseSummary("not json{"));

    [Fact]
    public void ParseSummary_returns_null_when_queries_missing()
        => Assert.Null(PiHoleParser.ParseSummary("{\"gravity\":{}}"));

    [Fact]
    public void ParseSummary_parses_valid_response()
    {
        var json = """
        {
          "queries": { "total": 50000, "blocked": 3000, "percent_blocked": 6.0 },
          "clients": { "active": 8 },
          "gravity": { "domains_being_blocked": 1234567 }
        }
        """;
        var s = PiHoleParser.ParseSummary(json);
        Assert.NotNull(s);
        Assert.Equal(50000, s.QueriesToday);
        Assert.Equal(3000,  s.BlockedToday);
        Assert.Equal(6.0,   s.BlockedPercent);
        Assert.Equal(8,     s.UniqueClients);
        Assert.Equal(1234567, s.GravityDomains);
    }

    [Fact]
    public void ParseSummary_tolerates_missing_optional_fields()
    {
        var json = """{"queries":{"total":100,"blocked":5,"percent_blocked":5.0}}""";
        var s = PiHoleParser.ParseSummary(json);
        Assert.NotNull(s);
        Assert.Equal(0, s.UniqueClients);
        Assert.Equal(0, s.GravityDomains);
    }

    // ── ParseBlocking ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseBlocking_returns_true_for_enabled_string()
        => Assert.True(PiHoleParser.ParseBlocking("{\"blocking\":\"enabled\"}"));

    [Fact]
    public void ParseBlocking_returns_false_for_disabled_string()
        => Assert.False(PiHoleParser.ParseBlocking("{\"blocking\":\"disabled\"}"));

    [Fact]
    public void ParseBlocking_still_handles_boolean_true()
        => Assert.True(PiHoleParser.ParseBlocking("{\"blocking\":true}"));

    [Fact]
    public void ParseBlocking_still_handles_boolean_false()
        => Assert.False(PiHoleParser.ParseBlocking("{\"blocking\":false}"));

    [Fact]
    public void ParseBlocking_returns_null_on_malformed_json()
        => Assert.Null(PiHoleParser.ParseBlocking("garbage"));

    // ── ParseTopDomains ───────────────────────────────────────────────────────

    [Fact]
    public void ParseTopDomains_returns_empty_on_malformed_json()
        => Assert.Empty(PiHoleParser.ParseTopDomains("not json"));

    [Fact]
    public void ParseTopDomains_returns_empty_when_array_missing()
        => Assert.Empty(PiHoleParser.ParseTopDomains("{\"other\":\"value\"}"));

    [Fact]
    public void ParseTopDomains_parses_entries()
    {
        // PiHole v6 uses "domains" key
        var json = """
        {
          "domains": [
            {"domain": "ads.evil.com", "count": 99},
            {"domain": "tracker.io",   "count": 42}
          ]
        }
        """;
        var list = PiHoleParser.ParseTopDomains(json);
        Assert.Equal(2, list.Count);
        Assert.Equal("ads.evil.com", list[0].Domain);
        Assert.Equal(99,             list[0].Hits);
        Assert.Equal("tracker.io",   list[1].Domain);
        Assert.Equal(42,             list[1].Hits);
    }

    // ── ParseUpstreams ────────────────────────────────────────────────────────

    [Fact]
    public void ParseUpstreams_returns_empty_on_malformed_json()
        => Assert.Empty(PiHoleParser.ParseUpstreams("bad"));

    [Fact]
    public void ParseUpstreams_returns_empty_when_path_missing()
        => Assert.Empty(PiHoleParser.ParseUpstreams("{\"config\":{\"dns\":{}}}"));

    [Fact]
    public void ParseUpstreams_parses_upstream_list()
    {
        var json = """
        {
          "config": {
            "dns": {
              "upstreams": ["8.8.8.8#53", "1.1.1.1#53"]
            }
          }
        }
        """;
        var list = PiHoleParser.ParseUpstreams(json);
        Assert.Equal(2, list.Count);
        Assert.Equal("8.8.8.8#53", list[0]);
        Assert.Equal("1.1.1.1#53", list[1]);
    }
}
