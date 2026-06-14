using System.Diagnostics;
using System.Text.RegularExpressions;
namespace Tower.Core.Metrics;

public static class SmartCollector {
    static string? First(string s, string pat) { var m = Regex.Match(s, pat, RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value.Trim() : null; }
    static int? Attr(string s, string name) {
        var m = Regex.Match(s, name + @"\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
    public static SmartInfo Parse(string outText, bool ssd) {
        var s = new SmartInfo { Ssd = ssd };
        s.Model = First(outText, @"Device Model:\s+(.+)") ?? First(outText, @"Product:\s+(.+)") ?? "";
        s.Serial = First(outText, @"Serial Number:\s+(.+)") ?? "";
        s.Firmware = First(outText, @"Firmware Version:\s+(.+)") ?? "";
        s.RotationRate = First(outText, @"Rotation Rate:\s+(.+)") ?? "";
        var h = Regex.Match(outText, @"overall-health.*?:\s+(\w+)", RegexOptions.IgnoreCase);
        s.Health = h.Success ? h.Groups[1].Value : "UNKNOWN";
        s.PowerOnHours = Attr(outText,"Power_On_Hours"); s.PowerCycles = Attr(outText,"Power_Cycle_Count");
        s.Reallocated = Attr(outText,"Reallocated_Sector_Ct"); s.Pending = Attr(outText,"Current_Pending_Sector");
        s.Uncorrectable = Attr(outText,"Offline_Uncorrectable"); s.Temp = Attr(outText,"Temperature_Celsius");
        s.Wear = Attr(outText,"Media_Wearout_Indicator");
        s.Alert = ComputeAlert(s); return s;
    }
    public static int ComputeAlert(SmartInfo d) {
        int a = 0;
        if (d.Health != "PASSED" && d.Health != "UNKNOWN") a = 2;
        if ((d.Uncorrectable ?? 0) > 0) a = 2;
        if ((d.Reallocated ?? 0) > 0) a = Math.Max(a,1);
        if ((d.Pending ?? 0) > 0) a = Math.Max(a,1);
        int t = d.Temp ?? 0;
        if (!d.Ssd && t > 60) a = Math.Max(a,2); else if (!d.Ssd && t > 50) a = Math.Max(a,1);
        if ((d.PowerOnHours ?? 0) > 50000) a = Math.Max(a,1);
        return a;
    }
    public static SmartInfo Collect(string device, bool ssd) {
        var psi = new ProcessStartInfo("sudo", $"/usr/sbin/smartctl -i -H -A {device}") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!; var outText = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(15000);
        if (string.IsNullOrWhiteSpace(outText)) return new SmartInfo { Ssd = ssd };
        return Parse(outText, ssd);
    }
}
