using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tower.Core.Maintenance;
using Tower.Core.State;

namespace Tower.Core.Workers;

public class SizeMonitorWorker(
    LiveState state,
    IOptions<TowerConfig> cfg,
    MaintenanceOptions maint) : BackgroundService
{
    private const int IntervalMs = 300_000; // 5 minutes

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sizes = Collect(cfg.Value, maint);
                state.SetSizes(sizes);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[SizeMonitorWorker] {ex.Message}");
            }

            await Task.Delay(IntervalMs, stoppingToken);
        }
    }

    // Internal so tests can call directly
    public static List<SizeInfo> Collect(TowerConfig config, MaintenanceOptions maint)
    {
        var list = new List<SizeInfo>();

        // Tower DB: tower.db (+ WAL/SHM if present, summed)
        const string towerDb = "tower.db";
        long towerDbBytes = 0;
        bool towerDbExists = false;
        foreach (var p in new[] { towerDb, "tower.db-wal", "tower.db-shm" })
        {
            if (File.Exists(p))
            {
                towerDbExists = true;
                towerDbBytes += new FileInfo(p).Length;
            }
        }
        list.Add(new SizeInfo("Tower DB", Path.GetFullPath(towerDb), towerDbBytes, towerDbExists));

        // Maintenance log
        {
            var p = maint.LogPath;
            bool exists = File.Exists(p);
            long bytes = exists ? new FileInfo(p).Length : 0;
            list.Add(new SizeInfo("Maintenance Log", Path.GetFullPath(p), bytes, exists));
        }

        // Per-project DBs
        foreach (var proj in config.Projects)
        {
            if (string.IsNullOrWhiteSpace(proj.DbPath)) continue;
            bool exists = File.Exists(proj.DbPath);
            long bytes = exists ? new FileInfo(proj.DbPath).Length : 0;
            list.Add(new SizeInfo($"{proj.Name} DB", proj.DbPath, bytes, exists));
        }

        return list;
    }
}
