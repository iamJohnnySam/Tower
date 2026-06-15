using System.Diagnostics;
namespace Tower.Core.Metrics;

public static class ServiceCollector {
    public static string Status(string name) {
        try {
            var psi = new ProcessStartInfo("systemctl", $"is-active {name}") {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return o;
        } catch { return "unknown"; }
    }
}
