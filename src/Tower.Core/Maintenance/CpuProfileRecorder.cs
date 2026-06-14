using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Models;
using Tower.Core.State;

namespace Tower.Core.Maintenance;

/// <summary>
/// Background service that samples CPU every 60 s into a 168-slot (7×24)
/// weekly profile, and flushes accumulated averages to the DB every 5 minutes.
/// </summary>
public class CpuProfileRecorder(LiveState state, IServiceScopeFactory scopes) : BackgroundService
{
    private const int SlotCount   = 168;  // 7 days × 24 hours
    private const int SampleCapIn = 500;  // in-memory rolling cap per slot
    private const int SampleCapDb = 1000; // DB combined cap per slot

    // In-memory accumulators (not lock-protected — single background thread only)
    private readonly double[] _avg     = new double[SlotCount];
    private readonly int[]    _count   = new int[SlotCount];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int tickCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(60_000, stoppingToken);
                tickCount++;

                // ── Sample ─────────────────────────────────────────────────────
                try
                {
                    var now    = DateTime.Now;
                    int slot   = CpuProfile.SlotFor(now.DayOfWeek, now.Hour);
                    double cpu = state.Stats.CpuPct;

                    int n = Math.Min(_count[slot], SampleCapIn);
                    _avg[slot]   = ((_avg[slot] * n) + cpu) / (n + 1);
                    _count[slot] = n + 1;
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"[CpuProfileRecorder] sample: {ex.Message}");
                }

                // ── Flush every 5 ticks (≈5 minutes) ──────────────────────────
                if (tickCount % 5 == 0)
                {
                    await FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[CpuProfileRecorder] loop: {ex.Message}");
            }
        }

        // Final flush on shutdown
        try { await FlushAsync(); } catch { /* best-effort */ }
    }

    private async Task FlushAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();

            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (_count[slot] == 0) continue;

                double newAvg   = _avg[slot];
                int    newCount = _count[slot];

                var existing = await db.CpuProfile.FindAsync(slot);
                if (existing is null)
                {
                    db.CpuProfile.Add(new CpuProfileSlot
                    {
                        Slot        = slot,
                        AvgCpu      = newAvg,
                        SampleCount = Math.Min(newCount, SampleCapDb),
                    });
                }
                else
                {
                    // Weighted merge of existing DB average with in-memory average
                    int combinedCount = existing.SampleCount + newCount;
                    double combinedAvg = ((existing.AvgCpu * existing.SampleCount) +
                                         (newAvg * newCount)) / combinedCount;
                    existing.AvgCpu      = combinedAvg;
                    existing.SampleCount = Math.Min(combinedCount, SampleCapDb);
                }

                // Reset in-memory slot after flush
                _avg[slot]   = 0;
                _count[slot] = 0;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[CpuProfileRecorder] flush: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads all CpuProfileSlot rows and returns two 168-length arrays
    /// (avg CPU values and sample counts).  Used by the scheduler.
    /// </summary>
    public static (double[] cpu, int[] samples) LoadFromDb(TowerDbContext db)
    {
        var cpu     = new double[SlotCount];
        var samples = new int[SlotCount];

        foreach (var row in db.CpuProfile.AsNoTracking())
        {
            if (row.Slot >= 0 && row.Slot < SlotCount)
            {
                cpu[row.Slot]     = row.AvgCpu;
                samples[row.Slot] = row.SampleCount;
            }
        }

        return (cpu, samples);
    }
}
