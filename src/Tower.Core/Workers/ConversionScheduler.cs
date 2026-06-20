using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Conversion;
using Tower.Core.Data;
using Tower.Core.Maintenance;
using Tower.Core.State;

namespace Tower.Core.Workers;

/// <summary>
/// Background service that fires ConversionService.RunNextJobAsync on two conditions:
/// 1. During the low-CPU hour window from the weekly profile (first 10 minutes)
/// 2. When CPU has been below 30% for 15+ consecutive 60s ticks
/// </summary>
public class ConversionScheduler(
    ConversionService conversion,
    IServiceScopeFactory scopes,
    LiveState state) : BackgroundService
{
    private int _idleTicks = 0;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(60_000, ct);

                // Track consecutive idle ticks (CPU < 30%)
                if (state.Stats.CpuPct < 30.0)
                    _idleTicks++;
                else
                    _idleTicks = 0;

                // Skip if already converting
                if (conversion.IsConverting) continue;

                var now = DateTime.Now;
                bool shouldFire = false;

                // Condition 1: Window (within low-CPU hour, first 10 minutes)
                using (var scope = scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
                    var (cpu, samples) = CpuProfileRecorder.LoadFromDb(db);
                    int window = CpuProfile.BestWindow(cpu, samples);
                    shouldFire = IsInWindow(window, now.Hour, now.Minute);
                }

                // Condition 2: Opportunistic (15 consecutive minutes of low CPU)
                if (!shouldFire)
                    shouldFire = IsOpportunistic(_idleTicks);

                // Fire job if either condition met (fire-and-forget)
                if (shouldFire)
                    _ = Task.Run(() => conversion.RunNextJobAsync(ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[ConversionScheduler] {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns true if the current time is within the target window:
    /// the target hour and the first 10 minutes (minute < 10).
    /// </summary>
    public static bool IsInWindow(int targetHour, int currentHour, int currentMinute) =>
        currentHour == targetHour && currentMinute < 10;

    /// <summary>
    /// Returns true if idle ticks indicate opportunistic opportunity:
    /// 15 or more consecutive 60-second ticks with CPU < 30%.
    /// </summary>
    public static bool IsOpportunistic(int idleTicks) =>
        idleTicks >= 15;
}
