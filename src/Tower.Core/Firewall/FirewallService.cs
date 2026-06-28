using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tower.Core.Firewall;

/// <summary>
/// Reads ufw firewall state via the scoped passwordless rule in /etc/sudoers.d/tower
/// (`sudo /usr/sbin/ufw status verbose`). Read-only — Tower never mutates the firewall.
/// </summary>
public sealed partial class FirewallService
{
    [GeneratedRegex(@"^(?<to>.+?)\s{2,}(?<action>(?:ALLOW|DENY|LIMIT|REJECT)(?:\s+(?:IN|OUT|FWD))?)\s{2,}(?<from>.+?)(?:\s{2,}#\s?(?<comment>.*))?\s*$")]
    private static partial Regex RuleLine();

    public async Task<FirewallStatus> GetStatusAsync(CancellationToken ct = default)
    {
        string output;
        try
        {
            var psi = new ProcessStartInfo("sudo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("/usr/sbin/ufw");
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("verbose");

            using var p = Process.Start(psi)!;
            output     = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
                return new FirewallStatus { Error = string.IsNullOrWhiteSpace(stderr) ? $"ufw exited {p.ExitCode}" : stderr.Trim() };
        }
        catch (Exception ex)
        {
            return new FirewallStatus { Error = ex.Message };
        }

        return Parse(output);
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
