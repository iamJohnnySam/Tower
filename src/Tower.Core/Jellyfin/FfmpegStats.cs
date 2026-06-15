using System.Diagnostics;
using System.Globalization;
namespace Tower.Core.Jellyfin;
public static class FfmpegStats {
    public static (int count, double cpu) Parse(string psOutput) {
        int count = 0; double cpu = 0;
        foreach (var line in psOutput.Split('\n')) {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!parts[0].Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            if (double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) cpu += c;
        }
        return (count, Math.Round(cpu, 1));
    }
    public static (int count, double cpu) Collect() {
        try {
            var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-eo"); psi.ArgumentList.Add("comm,%cpu"); psi.ArgumentList.Add("--no-headers");
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
            return Parse(o);
        } catch { return (0, 0); }
    }
}
