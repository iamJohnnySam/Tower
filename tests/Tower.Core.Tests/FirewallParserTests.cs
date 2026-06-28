using Tower.Core.Firewall;

namespace Tower.Core.Tests;

public class FirewallParserTests
{
    // Representative `ufw status verbose` output, including the v6 duplicate rows,
    // a commented rule, a port-range rule, and a multi-word "To" column.
    private const string Sample = """
        Status: active
        Logging: on (low)
        Default: deny (incoming), allow (outgoing), disabled (routed)
        New profiles: skip

        To                         Action      From
        --                         ------      ----
        22/tcp                     ALLOW IN    Anywhere                   # SSH
        50000:60000/tcp            ALLOW IN    Anywhere
        5028/tcp                   ALLOW IN    Anywhere                   # Tester
        80/tcp                     DENY IN     192.168.1.0/24
        22/tcp (v6)                ALLOW IN    Anywhere (v6)              # SSH
        """;

    [Fact]
    public void Parse_reads_status_and_defaults()
    {
        var s = FirewallService.Parse(Sample);
        Assert.True(s.Active);
        Assert.Equal("deny", s.DefaultIncoming);
        Assert.Equal("allow", s.DefaultOutgoing);
        Assert.Equal("disabled", s.DefaultRouted);
        Assert.Equal("on (low)", s.Logging);
    }

    [Fact]
    public void Parse_reads_all_rules_including_v6()
    {
        var s = FirewallService.Parse(Sample);
        Assert.Equal(5, s.Rules.Count);
    }

    [Fact]
    public void Parse_captures_comment_and_columns()
    {
        var s = FirewallService.Parse(Sample);
        var tester = s.Rules.Single(r => r.Comment == "Tester");
        Assert.Equal("5028/tcp", tester.To);
        Assert.Equal("ALLOW IN", tester.Action);
        Assert.Equal("Anywhere", tester.From);
        Assert.False(tester.IsV6);
    }

    [Fact]
    public void Parse_flags_v6_rows()
    {
        var s = FirewallService.Parse(Sample);
        Assert.Contains(s.Rules, r => r.IsV6);
        Assert.Single(s.Rules.Where(r => r.IsV6));
    }

    [Fact]
    public void Parse_handles_port_range_without_comment()
    {
        var s = FirewallService.Parse(Sample);
        var range = s.Rules.Single(r => r.To == "50000:60000/tcp");
        Assert.Equal("ALLOW IN", range.Action);
        Assert.Null(range.Comment);
    }

    [Fact]
    public void Parse_reads_deny_action()
    {
        var s = FirewallService.Parse(Sample);
        var deny = s.Rules.Single(r => r.From == "192.168.1.0/24");
        Assert.Equal("DENY IN", deny.Action);
    }

    [Fact]
    public void Parse_inactive_when_status_not_active()
    {
        var s = FirewallService.Parse("Status: inactive");
        Assert.False(s.Active);
        Assert.Empty(s.Rules);
    }
}
