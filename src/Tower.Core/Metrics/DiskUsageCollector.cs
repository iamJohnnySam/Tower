using System.Diagnostics;
namespace Tower.Core.Metrics;

public record DuItem(string Path, string Name, long Size);

public static class DiskUsageCollector {
    public static List<DuItem> ParseDu(string outText, string root) {
        var items = new List<DuItem>();
        foreach (var line in outText.Split('\n')) {
            var parts = line.Split('\t', 2);
            if (parts.Length != 2) continue;
            if (parts[1] == root || !long.TryParse(parts[0], out var size)) continue;
            items.Add(new DuItem(parts[1], System.IO.Path.GetFileName(parts[1]), size));
        }
        return items.OrderByDescending(i => i.Size).ToList();
    }

    public static List<DuItem> Depth1(string root) {
        var psi = new ProcessStartInfo("du") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-xb");
        psi.ArgumentList.Add("--max-depth=1");
        psi.ArgumentList.Add(root);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(600000);
        return ParseDu(o, root);
    }
}
