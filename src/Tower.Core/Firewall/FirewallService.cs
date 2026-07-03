using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tower.Core.Firewall;

/// <summary>
/// Reads/writes ufw firewall state via the scoped passwordless rules in /etc/sudoers.d/tower
/// (`sudo /usr/sbin/ufw status verbose` and `allow|deny|limit|reject <port>`).
/// </summary>
public sealed partial class FirewallService
{
    [GeneratedRegex(@"^(?<to>.+?)\s{2,}(?<action>(?:ALLOW|DENY|LIMIT|REJECT)(?:\s+(?:IN|OUT|FWD))?)\s{2,}(?<from>.+?)(?:\s{2,}#\s?(?<comment>.*))?\s*$")]
    private static partial Regex RuleLine();

    public async Task<FirewallStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var (ok, output, err) = await RunUfwAsync(ct, "status", "verbose");
        if (!ok)
            return new FirewallStatus { Error = string.IsNullOrWhiteSpace(err) ? output : err };

        return Parse(output);
    }

    /// <summary>
    /// Adds a ufw rule. Inputs are validated (port + proto + action) so only safe,
    /// fixed-shape arguments ever reach ufw. Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> AddRuleAsync(int port, string proto, string action, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535)
            return "Port must be between 1 and 65535.";

        action = action.ToLowerInvariant();
        if (action is not ("allow" or "deny" or "limit" or "reject"))
            return "Action must be allow, deny, limit, or reject.";

        proto = proto.ToLowerInvariant();
        var target = proto switch
        {
            "tcp" or "udp" => $"{port}/{proto}",
            "both" or ""   => port.ToString(),
            _              => null,
        };
        if (target is null)
            return "Protocol must be tcp, udp, or both.";

        var (ok, output, err) = await RunUfwAsync(ct, action, target);
        return ok ? null : (string.IsNullOrWhiteSpace(err) ? output : err.Trim());
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunUfwAsync(CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sudo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("/usr/sbin/ufw");
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            return (p.ExitCode == 0, stdout.Trim(), p.ExitCode == 0 ? "" : (string.IsNullOrWhiteSpace(stderr) ? $"ufw exited {p.ExitCode}" : stderr));
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    public static FirewallStatus Parse(string output)
    {
        bool    active = false;
        string? logging = null, dIn = null, dOut = null, dRouted = null;
        var     rules = new List<FirewallRule>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("Status:", StringComparison.Ordinal))
            {
                active = line["Status:".Length..].Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (line.StartsWith("Logging:", StringComparison.Ordinal))
            {
                logging = line["Logging:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("Default:", StringComparison.Ordinal))
            {
                // Default: deny (incoming), allow (outgoing), disabled (routed)
                foreach (var part in line["Default:".Length..].Split(','))
                {
                    var m = Regex.Match(part.Trim(), @"^(\S+)\s*\((\w+)\)$");
                    if (!m.Success) continue;
                    switch (m.Groups[2].Value)
                    {
                        case "incoming": dIn     = m.Groups[1].Value; break;
                        case "outgoing": dOut    = m.Groups[1].Value; break;
                        case "routed":   dRouted = m.Groups[1].Value; break;
                    }
                }
                continue;
            }

            // Skip header / separator rows.
            if (line.StartsWith("To", StringComparison.Ordinal) ||
                line.StartsWith("--", StringComparison.Ordinal) ||
                line.StartsWith("New profiles:", StringComparison.Ordinal))
                continue;

            var rm = RuleLine().Match(line);
            if (!rm.Success) continue;

            var comment = rm.Groups["comment"].Success ? rm.Groups["comment"].Value.Trim() : null;
            rules.Add(new FirewallRule
            {
                To      = rm.Groups["to"].Value.Trim(),
                Action  = rm.Groups["action"].Value.Trim(),
                From    = rm.Groups["from"].Value.Trim(),
                Comment = string.IsNullOrWhiteSpace(comment) ? null : comment,
            });
        }

        return new FirewallStatus
        {
            Active          = active,
            Logging         = logging,
            DefaultIncoming = dIn,
            DefaultOutgoing = dOut,
            DefaultRouted   = dRouted,
            Rules           = rules,
        };
    }
}
