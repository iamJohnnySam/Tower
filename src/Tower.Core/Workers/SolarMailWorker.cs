using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Data;
using Tower.Core.Gmail;
using Tower.Core.Settings;
using Tower.Core.Solar;

namespace Tower.Core.Workers;

public class SolarMailWorker(IServiceScopeFactory scopes) : BackgroundService
{
    private const int IntervalMs = 43_200_000; // 12 hours

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
        known.UnionWith(db.SolarAlarms.Select(a => a.GmailMessageId));

        // Full sweep of the label (no date bound): imported emails are trashed and leave
        // the label, so it stays small — and this guarantees older items get picked up too.
        var ids = await reader.ListMessageIdsAsync(labelId!, null, ct);

        int imported = 0;
        string? lastError = null;
        foreach (var id in ids)
        {
            try
            {
                if (known.Contains(id))
                {
                    // Already imported on a prior run — ensure it's out of the label (retry a failed trash).
                    await reader.TrashMessageAsync(id, ct);
                    continue;
                }
                var msg = await reader.GetMessageAsync(id, ct);
                if (msg == null) continue;

                // A message is either a Plant report, an Abnormal Power Reminder, or neither.
                var report = SolarReportParser.Parse(msg.Value.Subject, msg.Value.Body);
                if (report != null)
                {
                    report.GmailMessageId = id;
                    report.ImportedAt = DateTime.UtcNow;
                    db.SolarReports.Add(report);
                }
                else
                {
                    var alarm = SolarAlarmParser.Parse(msg.Value.Subject, msg.Value.Body, msg.Value.Date);
                    if (alarm == null) continue;   // leave unrecognized emails in place for inspection
                    alarm.GmailMessageId = id;
                    alarm.ImportedAt = DateTime.UtcNow;
                    db.SolarAlarms.Add(alarm);
                }
                db.SaveChanges();
                imported++;
                await reader.TrashMessageAsync(id, ct);   // delete (trash) once safely imported
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        await BackfillWeatherAsync(scope, db, ct);

        settings.Set("solar.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("solar.mail.last_count", imported.ToString());
        settings.Set("solar.mail.last_error", lastError);
        return imported;
    }

    // Fetch Colombo daily irradiance for the span of daily reports and store any missing days
    // (add-only; never overwrites or deletes existing weather rows).
    private static async Task BackfillWeatherAsync(IServiceScope scope, TowerDbContext db, CancellationToken ct)
    {
        var dates = db.SolarReports
            .Where(r => r.ReportType == Tower.Core.Models.SolarReportType.Daily)
            .Select(r => r.PeriodEnd)
            .ToList();
        if (dates.Count == 0) return;

        var start = dates.Min().Date;
        var end = dates.Max().Date;
        var have = db.SolarWeather.Select(w => w.Date).ToHashSet();

        var weather = scope.ServiceProvider.GetRequiredService<WeatherClient>();
        var fetched = await weather.GetDailyAsync(start, end, ct);
        foreach (var w in fetched)
            if (!have.Contains(w.Date))
                db.SolarWeather.Add(w);
        db.SaveChanges();
    }
}
