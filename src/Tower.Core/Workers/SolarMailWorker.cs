using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Gmail;
using Tower.Core.Settings;
using Tower.Core.Solar;

namespace Tower.Core.Workers;

public class SolarMailWorker(IServiceScopeFactory scopes) : BackgroundService
{
    private const int IntervalMs = 1_800_000; // 30 minutes

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { await Console.Error.WriteLineAsync($"[SolarMailWorker] {ex.Message}"); }
            await Task.Delay(IntervalMs, stoppingToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var tokens = scope.ServiceProvider.GetRequiredService<GmailTokenService>();
        var reader = scope.ServiceProvider.GetRequiredService<GmailReader>();
        var labelId = settings.Get("gmail.label_id");
        if (!tokens.IsConnected || string.IsNullOrWhiteSpace(labelId)) return 0;

        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var known = db.SolarReports.Select(r => r.GmailMessageId).ToHashSet();

        // Bound the query with a lookback window to keep pages small.
        var after = DateTime.UtcNow.AddDays(-45);
        var ids = await reader.ListMessageIdsAsync(labelId!, after, ct);

        int imported = 0;
        string? lastError = null;
        foreach (var id in ids)
        {
            if (known.Contains(id)) continue;
            try
            {
                var msg = await reader.GetMessageAsync(id, ct);
                if (msg == null) continue;
                var report = SolarReportParser.Parse(msg.Value.Subject, msg.Value.Body);
                if (report == null) continue;
                report.GmailMessageId = id;
                report.ImportedAt = DateTime.UtcNow;
                db.SolarReports.Add(report);
                db.SaveChanges();
                imported++;
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        settings.Set("solar.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("solar.mail.last_count", imported.ToString());
        settings.Set("solar.mail.last_error", lastError);
        return imported;
    }
}
