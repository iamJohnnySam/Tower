using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Settings;
using Tower.Core.Solar;

namespace Tower.Core.Workers;

public class SolaxPollWorker(IServiceScopeFactory scopes) : BackgroundService
{
    private const int IntervalMs = 300_000; // 5 minutes (288/day, under 10k/day limit)

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var tokenId = settings.Get("solax.token_id");
                var sn = settings.Get("solax.sn");

                if (!string.IsNullOrWhiteSpace(tokenId) && !string.IsNullOrWhiteSpace(sn))
                {
                    var client = scope.ServiceProvider.GetRequiredService<SolaxClient>();
                    var snap = await client.GetRealtimeAsync(tokenId!, sn!, stoppingToken);
                    if (snap != null)
                    {
                        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
                        // Dedup: skip if inverter's uploadTime unchanged from the latest row.
                        var lastUpload = db.SolarSnapshots
                            .OrderByDescending(x => x.CapturedAt)
                            .Select(x => x.UploadTime)
                            .FirstOrDefault();
                        if (snap.UploadTime != lastUpload)
                        {
                            db.SolarSnapshots.Add(snap);
                            db.SaveChanges();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[SolaxPollWorker] {ex.Message}");
            }
            await Task.Delay(IntervalMs, stoppingToken);
        }
    }
}
