using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Tower.Core.Metrics;
using Tower.Core.State;

namespace Tower.Core.Workers;

public class SmartWorker(LiveState state) : BackgroundService
{
    private const int IntervalMs = 300_000; // 5 minutes

    // Regex for virtual/non-physical block devices to skip
    private static readonly Regex SkipPattern =
        new(@"^(loop|dm|md|ram|zram|sr|fd)", RegexOptions.Compiled);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately on first start, then on interval
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var disks = CollectAllDisks();
                state.SetDisks(disks);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[SmartWorker] {ex.Message}");
            }

            await Task.Delay(IntervalMs, stoppingToken);
        }
    }

    // Internal so tests can call directly
    public static List<SmartInfo> CollectAllDisks()
    {
        var result = new List<SmartInfo>();

        try
        {
            var sysBlock = "/sys/block";
            if (!Directory.Exists(sysBlock)) return result;

            foreach (var entry in Directory.EnumerateDirectories(sysBlock))
            {
                var name = Path.GetFileName(entry);
                if (SkipPattern.IsMatch(name)) continue;

                try
                {
                    bool ssd = IsRotationalSsd(name);
                    var  info = SmartCollector.Collect($"/dev/{name}", ssd);
                    result.Add(info);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SmartWorker] /dev/{name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SmartWorker] enumerate /sys/block: {ex.Message}");
        }

        return result;
    }

    private static bool IsRotationalSsd(string blockName)
    {
        try
        {
            var path = $"/sys/block/{blockName}/queue/rotational";
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path).Trim();
            return text == "0"; // "0" = not rotational = SSD
        }
        catch { return false; }
    }
}
