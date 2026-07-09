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

        // Full sweep of the label (no date bound): imported emails are trashed and leave
        // the label, so it stays small — and this guarantees older reports get picked up too.
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
                var report = SolarReportParser.Parse(msg.Value.Subject, msg.Value.Body);
                if (report == null) continue;   // leave unparseable emails in place for inspection
                report.GmailMessageId = id;
                report.ImportedAt = DateTime.UtcNow;
                db.SolarReports.Add(report);
                db.SaveChanges();
                imported++;
                await reader.TrashMessageAsync(id, ct);   // delete (trash) once safely imported
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        settings.Set("solar.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("solar.mail.last_count", imported.ToString());
        settings.Set("solar.mail.last_error", lastError);
        return imported;
    }
}
