using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Maintenance;
using Tower.Core.Settings;

namespace Tower.Core.Workers;

/// <summary>
/// Checks every 60 s whether it's time to run maintenance (in the low-CPU
/// window computed from the weekly CPU profile) and fires MaintenanceRunner.
/// </summary>
public class MaintenanceScheduler(MaintenanceRunner runner, IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(60_000, stoppingToken);

                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var db  = scope.ServiceProvider.GetRequiredService<TowerDbContext>();

                // Not enabled → skip
                if (svc.Get("maint.enabled") != "true")
                    continue;

                var now   = DateTime.Now;
                string today = now.ToString("yyyy-MM-dd");

                // Already ran today → skip
                if (svc.Get("maint.last_date") == today)
                    continue;

                // Determine the target window hour
                int window;
                var windowSetting = svc.Get("maint.window_hour");
                if (windowSetting is not null && int.TryParse(windowSetting, out int parsedHour))
                {
                    window = parsedHour;
                }
                else
                {
                    var (cpu, samples) = CpuProfileRecorder.LoadFromDb(db);
                    window = CpuProfile.BestWindow(cpu, samples);
                }

                // Fire if we're in the target hour (within the first 10 minutes)
                if (now.Hour == window && now.Minute < 10)
                {
                    _ = Task.Run(() => runner.Run("scheduler"));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[MaintenanceScheduler] {ex.Message}");
            }
        }
    }
}
