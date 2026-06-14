using System.Diagnostics;
using System.Text.RegularExpressions;
namespace Tower.Core.Metrics;

public record GpuStats(
    string Type,
    string Name = "",
    double? Usage = null,
    long? MemUsed = null,
    long? MemTotal = null,
    double? Temp = null,
    double? Power = null,
    int? Freq = null);

public static class GpuCollector {
    static GpuStats? _cache;
    static DateTime _cacheTime = DateTime.MinValue;
    const int CacheSeconds = 5;

    public static GpuStats Collect() {
        if (_cache != null && (DateTime.UtcNow - _cacheTime).TotalSeconds < CacheSeconds)
            return _cache;
        var result = CollectInner();
        _cache = result;
        _cacheTime = DateTime.UtcNow;
        return result;
    }

    static GpuStats CollectInner() {
        try {
            // --- Try nvidia-smi first ---
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw --format=csv,noheader,nounits") {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null) {
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(o)) {
                    var fields = o.Trim().Split(',');
                    if (fields.Length >= 6) {
                        var name    = fields[0].Trim();
                        double.TryParse(fields[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var usage);
                        long.TryParse(fields[2].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var memUsedMib);
                        long.TryParse(fields[3].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var memTotalMib);
                        double.TryParse(fields[4].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temp);
                        double.TryParse(fields[5].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var power);
                        return new GpuStats(
                            Type: "nvidia",
                            Name: name,
                            Usage: usage,
                            MemUsed: memUsedMib * 1024 * 1024,
                            MemTotal: memTotalMib * 1024 * 1024,
                            Temp: temp,
                            Power: power);
                    }
                }
            }
        } catch { /* nvidia-smi absent or failed — fall through */ }

        try {
            // --- Fall back to Intel/AMD via lspci ---
            var psi = new ProcessStartInfo("lspci") {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null) {
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var line = o.Split('\n')
                    .FirstOrDefault(l => Regex.IsMatch(l, @"VGA|3D controller|Display controller", RegexOptions.IgnoreCase));
                if (line != null) {
                    // Extract the device name after the class prefix, e.g. "00:02.0 VGA compatible controller: Intel Corporation ..."
                    var m = Regex.Match(line, @":\s+(.+)$");
                    var gpuName = m.Success ? m.Groups[1].Value.Trim() : line.Trim();

                    int? freq = null;
                    try {
                        var freqPath = "/sys/class/drm/card0/gt_cur_freq_mhz";
                        if (System.IO.File.Exists(freqPath)) {
                            var freqTxt = System.IO.File.ReadAllText(freqPath).Trim();
                            if (int.TryParse(freqTxt, out var f)) freq = f;
                        }
                    } catch { /* ignore */ }

                    return new GpuStats(Type: "intel", Name: gpuName, Freq: freq);
                }
            }
        } catch { /* lspci absent or failed */ }

        return new GpuStats("none");
    }
}
