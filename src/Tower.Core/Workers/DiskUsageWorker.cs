using Microsoft.Extensions.Hosting;
using Tower.Core.Metrics;
using Tower.Core.State;

namespace Tower.Core.Workers;

public class DiskUsageWorker(LiveState state) : BackgroundService
{
    private const int IntervalMs = 600_000; // 10 minutes

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately on first start, then on interval
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (root, var, projects) = await Task.Run(Collect, stoppingToken);
                state.SetUsage(root, var, projects);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[DiskUsageWorker] {ex.Message}");
            }

            await Task.Delay(IntervalMs, stoppingToken);
        }
    }

    // Internal so tests can call directly
    public static (List<DuItem> Root, List<DuItem> Var, List<DuItem> Projects) Collect()
    {
        var root     = DiskUsageCollector.Depth1("/").Take(12).ToList();
        var var_     = DiskUsageCollector.Depth1("/var").Take(12).ToList();
        var projects = DiskUsageCollector.Depth1("/home/atom/dev");
        return (root, var_, projects);
    }
}
