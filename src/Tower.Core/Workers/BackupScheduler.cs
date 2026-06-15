using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Backup;
using Tower.Core.Projects;
using Tower.Core.Settings;
using Tower.Core.State;

namespace Tower.Core.Workers;

/// <summary>
/// Runs at startup and then every 60 s.
/// When the configured backup schedule time (HH:mm) matches the current clock minute
/// and the backup has not already run today, backs up every project that has a DbPath
/// plus tower.db, and writes results into <see cref="LiveState"/>.
/// </summary>
public class BackupScheduler(
    IServiceScopeFactory scopes,
    LiveState state,
    ProjectsOptions projOpts) : BackgroundService
{
    // Tracks the calendar date (yyyy-MM-dd) on which the last backup ran.
    private string? _lastBackupDate;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunTickAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[backup] {ex.Message}");
            }

            try { await Task.Delay(60_000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var schedule = settings.Get("backup.schedule") ?? "02:00";
        var token    = settings.Get("dropbox.access_token") ?? "";

        // Skip if no Dropbox token is configured.
        if (string.IsNullOrWhiteSpace(token))
            return;

        // Parse "HH:mm" schedule.
        if (!TryParseSchedule(schedule, out int h, out int m))
            return;

        var now    = DateTime.Now;
        var today  = now.ToString("yyyy-MM-dd");
        var target = new DateTime(now.Year, now.Month, now.Day, h, m, 0);

        // Fire on the first tick at-or-after the scheduled time today, once per calendar day.
        // This avoids missed backups if the process restarts or a tick overshoots the exact minute.
        if (_lastBackupDate == today || now < target)
            return;

        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();

        // Build the list of databases to back up:
        // one entry per project that has a non-empty DbPath, plus tower.db.
        var targets = new List<(string Name, string DbPath)>();

        foreach (var p in projOpts.Projects)
        {
            if (!string.IsNullOrWhiteSpace(p.DbPath))
                targets.Add((p.Name, p.DbPath));
        }

        // tower.db lives next to the running executable by convention.
        var towerDb = Path.Combine(AppContext.BaseDirectory, "tower.db");
        targets.Add(("Tower", towerDb));

        foreach (var (name, dbPath) in targets)
        {
            var result = await backup.BackupAsync(name, dbPath, token, ct);
            state.SetBackup(result);
        }

        _lastBackupDate = today;
    }

    private static bool TryParseSchedule(string schedule, out int hour, out int minute)
    {
        hour   = 0;
        minute = 0;

        if (string.IsNullOrWhiteSpace(schedule))
            return false;

        var parts = schedule.Trim().Split(':');
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out hour) && int.TryParse(parts[1], out minute);
    }
}
