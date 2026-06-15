using System.Diagnostics;
using System.Text.RegularExpressions;
namespace Tower.Core.Metrics;

public record NoIpStatus(
    string Status,
    string? CurrentIp = null,
    string? LastCheck = null,
    string? LastUpdate = null,
    string? PrevIp = null);

public static class NoIpCollector {
    static NoIpStatus? _cache;
    static DateTime _cacheTime = DateTime.MinValue;
    const int CacheSeconds = 30;

    public static NoIpStatus Collect() {
        if (_cache != null && (DateTime.UtcNow - _cacheTime).TotalSeconds < CacheSeconds)
            return _cache;
        var result = CollectInner();
        _cache = result;
        _cacheTime = DateTime.UtcNow;
        return result;
    }

    static NoIpStatus CollectInner() {
        try {
            var svcStatus = ServiceCollector.Status("noip");

            // --- Last check: most recent "checking ip again" line from recent journal ---
            string? lastCheck = null;
            try {
                var psi = new ProcessStartInfo("journalctl", "-u noip --no-pager -n 30") {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                // Find last line containing "checking ip again"
                var checkLine = o.Split('\n')
                    .LastOrDefault(l => l.Contains("checking ip again", StringComparison.OrdinalIgnoreCase));
                if (checkLine != null) {
                    // journalctl timestamps look like: "Jun 14 06:01:23 hostname noip[123]: ..."
                    var m = Regex.Match(checkLine, @"^(\w{3}\s+\d+\s+\d+:\d+:\d+)");
                    if (m.Success) lastCheck = m.Groups[1].Value;
                }
            } catch { /* ignore */ }

            // --- Last update + CurrentIp + PrevIp via grep pipeline ---
            string? lastUpdate = null;
            string? currentIp = null;
            string? prevIp = null;
            try {
                var psi = new ProcessStartInfo("bash") {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("journalctl -u noip --no-pager | grep 'update successful' | tail -1");
                using var p = Process.Start(psi)!;
                var line = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                if (!string.IsNullOrWhiteSpace(line)) {
                    var ts = Regex.Match(line, @"^(\w{3}\s+\d+\s+\d+:\d+:\d+)");
                    if (ts.Success) lastUpdate = ts.Groups[1].Value;
                    var cur = Regex.Match(line, @"current=(\d+\.\d+\.\d+\.\d+)");
                    if (cur.Success) currentIp = cur.Groups[1].Value;
                    var prev = Regex.Match(line, @"previous=(\d+\.\d+\.\d+\.\d+)");
                    if (prev.Success) prevIp = prev.Groups[1].Value;
                }
            } catch { /* ignore */ }

            return new NoIpStatus(
                Status: svcStatus,
                CurrentIp: currentIp,
                LastCheck: lastCheck,
                LastUpdate: lastUpdate,
                PrevIp: prevIp);
        } catch {
            return new NoIpStatus("unknown");
        }
    }
}
