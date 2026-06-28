namespace Tower.Core.Firewall;

public sealed class FirewallStatus
{
    public bool             Active          { get; init; }
    public string?          DefaultIncoming { get; init; }
    public string?          DefaultOutgoing { get; init; }
    public string?          DefaultRouted   { get; init; }
    public string?          Logging         { get; init; }
    public List<FirewallRule> Rules         { get; init; } = new();
    public DateTime         FetchedAt       { get; init; } = DateTime.Now;
    public string?          Error           { get; init; }
}

public sealed class FirewallRule
{
    public required string To      { get; init; }
    public required string Action  { get; init; }
    public required string From    { get; init; }
    public string?         Comment { get; init; }

    // True for the IPv6 duplicate rows ufw prints as "Anywhere (v6)".
    public bool IsV6 => To.Contains("(v6)") || From.Contains("(v6)");
}
